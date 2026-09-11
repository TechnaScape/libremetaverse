/*
 * Copyright (c) 2026, Sjofn LLC.
 * All rights reserved.
 *
 * - Redistribution and use in source and binary forms, with or without
 *   modification, are permitted provided that the following conditions are met:
 *
 * - Redistributions of source code must retain the above copyright notice, this
 *   list of conditions and the following disclaimer.
 * - Neither the name of the openmetaverse.co nor the names
 *   of its contributors may be used to endorse or promote products derived from
 *   this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
 * LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
 * CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
 * SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
 * CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
 * POSSIBILITY OF SUCH DAMAGE.
 */

using System.Collections.Generic;

namespace LibreMetaverse.Animesh
{
    /// <summary>
    /// Manages the set of animations currently playing on a single Animesh object and
    /// produces a blended <see cref="JointPose"/> dictionary each frame.
    /// </summary>
    public sealed class AnimeshPlayer
    {
        /// <summary>Full UUID of the in-world object this player drives.</summary>
        public UUID ObjectID { get; }

        // Active tracks keyed by animation UUID for O(1) add/remove.
        private readonly Dictionary<UUID, AnimationTrack> _tracks = new Dictionary<UUID, AnimationTrack>();
        private readonly object _lock = new object();

        /// <summary>Last host frame interval; read on demand to distinguish stalls from restarts.</summary>
        public float LastFrameSeconds { get; private set; }
        public float MaximumFrameSeconds { get; private set; }
        public long FramesOver100Milliseconds { get; private set; }

        internal AnimeshPlayer(UUID objectID)
        {
            ObjectID = objectID;
        }

        // ── Track management ──────────────────────────────────────────────────

        /// <summary>
        /// Returns the track for <paramref name="animationID"/>, creating it if needed.
        /// Called by <see cref="AnimeshManager"/> when the server signals a new animation.
        /// </summary>
        internal AnimationTrack GetOrAddTrack(UUID animationID)
            => GetOrAddTrack(animationID, 0);

        /// <summary>
        /// Returns the active track, preserving active playback across sequence re-signals.
        /// </summary>
        internal AnimationTrack GetOrAddTrack(UUID animationID, int sequence)
        {
            lock (_lock)
            {
                if (!_tracks.TryGetValue(animationID, out var track))
                {
                    track = new AnimationTrack(animationID, sequence);
                    _tracks[animationID] = track;
                }
                else
                {
                    track.AcceptSequence(sequence);
                }
                return track;
            }
        }

        /// <summary>
        /// Applies a completed asset request only if the animation is still signalled.
        /// </summary>
        /// <remarks>
        /// ObjectAnimation is a complete replacement state.  A download can finish after its
        /// animation was stopped; recreating the removed track here resurrects stale motion until
        /// another packet happens to replace it.
        /// </remarks>
        internal bool TryApplyData(UUID animationID, BinBVHAnimationReader data)
        {
            if (data == null) return false;

            lock (_lock)
            {
                if (!_tracks.TryGetValue(animationID, out var track)) return false;
                track.Data = data;
                return true;
            }
        }

        /// <summary>
        /// Applies one complete network state atomically with respect to pose evaluation.
        /// Returns only tracks whose asset still needs downloading.
        /// </summary>
        internal AnimationTrack[] ApplyAnimations(IReadOnlyList<Animation> animations)
        {
            lock (_lock)
            {
                var active = new HashSet<UUID>();
                var pending = new List<AnimationTrack>();
                for (int i = 0; i < animations.Count; i++)
                {
                    Animation animation = animations[i];
                    if (animation.AnimationID == UUID.Zero || !active.Add(animation.AnimationID)) continue;
                    AnimationTrack track = GetOrAddTrack(animation.AnimationID, animation.AnimationSequence);
                    if (track.Data == null) pending.Add(track);
                }
                // The old RetainOnly / GetOrAdd calls released the lock between each operation.
                // A render frame could observe half a packet, including an empty intermediate
                // pose during a replacement, and restore joints to their bind pose for a frame.
                RetainOnly(active);
                return pending.ToArray();
            }
        }

        /// <summary>
        /// Removes any tracks whose animation IDs are not in <paramref name="activeIDs"/>.
        /// Called when the server sends a complete replacement animation state.
        /// </summary>
        internal void RetainOnly(ISet<UUID> activeIDs)
        {
            lock (_lock)
            {
                var toRemove = new List<UUID>();
                foreach (var id in _tracks.Keys)
                    if (!activeIDs.Contains(id))
                        toRemove.Add(id);
                foreach (var id in toRemove)
                    _tracks.Remove(id);
            }
        }

        /// <summary>
        /// Returns the number of active tracks (including those waiting for asset data).
        /// </summary>
        public int TrackCount
        {
            get { lock (_lock) { return _tracks.Count; } }
        }

        /// <summary>
        /// Detached playback clocks for an on-demand diagnostic. Evaluating or advancing these
        /// tracks cannot change the player. Their decoded asset data must be treated as read-only.
        /// Array order preserves the player's actual merge order, including priority ties.
        /// </summary>
        public AnimationTrack[] SnapshotTracks()
        {
            lock (_lock)
            {
                var result = new AnimationTrack[_tracks.Count];
                int i = 0;
                foreach (AnimationTrack track in _tracks.Values)
                    result[i++] = track.DiagnosticSnapshot();
                return result;
            }
        }

        // ── Playback ──────────────────────────────────────────────────────────

        /// <summary>
        /// Advance all animation clocks by <paramref name="dt"/> seconds.
        /// Call once per frame from your render/simulation loop.
        /// Finished non-looping animations remain in the track list; they will be removed
        /// on the next <see cref="RetainOnly"/> call from the server.
        /// </summary>
        public void Update(float dt)
        {
            if (float.IsNaN(dt) || float.IsInfinity(dt) || dt < 0f) return;
            lock (_lock)
            {
                LastFrameSeconds = dt;
                if (dt > MaximumFrameSeconds) MaximumFrameSeconds = dt;
                if (dt > .1f) FramesOver100Milliseconds++;
                foreach (var track in _tracks.Values)
                    track.Advance(dt);
            }
        }

        /// <summary>
        /// Evaluates the blended pose for all active, loaded tracks at their current times.
        /// </summary>
        /// <returns>
        /// A dictionary mapping joint names to their blended <see cref="JointPose"/>.
        /// Joints not driven by any active animation are absent from the dictionary;
        /// <see cref="AnimeshSkinning"/> treats absent joints as being in bind pose.
        /// </returns>
        public Dictionary<string, JointPose> EvaluatePose()
        {
            var pose = new Dictionary<string, JointPose>();
            EvaluatePose(pose);
            return pose;
        }

        /// <summary>
        /// Evaluates into a caller-owned dictionary so render loops can avoid one allocation per
        /// animated object per frame.
        /// </summary>
        public void EvaluatePose(Dictionary<string, JointPose> pose)
        {
            if (pose == null) throw new System.ArgumentNullException(nameof(pose));
            pose.Clear();
            lock (_lock)
            {
                foreach (var track in _tracks.Values)
                    track.EvaluatePose(pose);
            }
        }
    }
}
