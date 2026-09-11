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

using System;
using System.Collections.Generic;

namespace LibreMetaverse.Animesh
{
    /// <summary>
    /// Manages playback state for a single <see cref="BinBVHAnimationReader"/> animation
    /// and evaluates per-joint poses via keyframe interpolation.
    /// </summary>
    public sealed class AnimationTrack
    {
        /// <summary>UUID of the animation asset.</summary>
        public UUID AnimationID { get; }

        /// <summary>The most recently received simulator sequence for this animation.</summary>
        /// <remarks>
        /// The UUID identifies the asset, not one playback.  A script can restart the same
        /// animation asset with a new sequence number, and retaining the finished clock from the
        /// previous playback leaves the animated object frozen at its last keyframe.
        /// </remarks>
        public int Sequence { get; private set; }

        /// <summary>Re-signals received, including those that correctly kept playing.</summary>
        public int SequenceChanges { get; private set; }

        /// <summary>Finished motions actually restarted by a new sequence.</summary>
        public int Restarts { get; private set; }

        /// <summary>
        /// Parsed animation data.  Null until the asset has been downloaded and decoded.
        /// Joints are evaluated only when this is non-null.
        /// </summary>
        public BinBVHAnimationReader? Data { get; internal set; }

        /// <summary>Current playback position in seconds.</summary>
        public float CurrentTime { get; private set; }

        /// <summary>Time since this playback started; does not wrap at a loop boundary.</summary>
        public float ElapsedTime { get; private set; }

        /// <summary>True once a non-looping animation has played its full duration.</summary>
        public bool IsFinished { get; private set; }

        // Diagnostics must not advance the live playback clock. Decoded asset data is shared
        // read-only; sequence, playback time and finished state are detached.
        internal AnimationTrack DiagnosticSnapshot() => new AnimationTrack(AnimationID, Sequence)
        {
            Data = Data, CurrentTime = CurrentTime, ElapsedTime = ElapsedTime, IsFinished = IsFinished,
            SequenceChanges = SequenceChanges, Restarts = Restarts,
        };

        internal AnimationTrack(UUID id, int sequence = 0)
        {
            AnimationID = id;
            Sequence = sequence;
        }

        /// <summary>Accepts a re-signal, restarting only an already finished motion.</summary>
        /// <returns>True when the clock was restarted.</returns>
        internal bool AcceptSequence(int sequence)
        {
            if (sequence == Sequence) return false;

            Sequence = sequence;
            SequenceChanges++;
            // A new sequence is a request to start, not proof the current motion stopped.
            // LLControlAvatar uses the same LLMotionController as ordinary avatars; its
            // startMotion lets an active motion continue (llmotioncontroller.cpp:430).
            // Resetting here makes a looping pet jump to its first frame whenever its script
            // re-signals it. A genuinely removed track is recreated by the player instead.
            if (!IsFinished) return false;

            Restarts++;
            CurrentTime = 0f;
            ElapsedTime = 0f;
            IsFinished = false;
            return true;
        }

        /// <summary>
        /// Advance the playback clock by <paramref name="dt"/> seconds.
        /// Loops when <see cref="BinBVHAnimationReader.Loop"/> is true;
        /// clamps and marks finished otherwise.
        /// </summary>
        public void Advance(float dt)
        {
            if (Data == null || IsFinished) return;
            dt = Math.Max(0f, dt);
            ElapsedTime += dt;
            CurrentTime += dt;

            if (Data.Loop)
            {
                float span = Data.OutPoint - Data.InPoint;
                if (span > 0f && CurrentTime > Data.OutPoint)
                    CurrentTime = Data.InPoint + (CurrentTime - Data.InPoint) % span;
                else if (span <= 0f)
                    CurrentTime = Math.Min(CurrentTime, Math.Max(0f, Data.Length));
            }
            else if (CurrentTime >= Data.Length)
            {
                CurrentTime = Math.Max(0f, Data.Length);
                IsFinished = true;
            }
        }

        /// <summary>
        /// Current blend weight [0, 1] factoring in the ease-in and ease-out curves
        /// specified by the animation metadata.
        /// </summary>
        public float EaseWeight
        {
            get
            {
                if (Data == null) return 0f;
                float t = ElapsedTime;
                if (Data.EaseInTime > 0f && t < Data.EaseInTime)
                    return t / Data.EaseInTime;
                // A loop (including a zero-duration static pose) stays active until the
                // simulator removes it. The loop endpoint is not a request to ease out.
                float easeOutStart = Data.Length - Data.EaseOutTime;
                if (!Data.Loop && Data.EaseOutTime > 0f && t > easeOutStart)
                    return Math.Max(0f, (Data.Length - t) / Data.EaseOutTime);
                return 1f;
            }
        }

        /// <summary>
        /// Evaluate the animation at <see cref="CurrentTime"/> and merge each joint's
        /// interpolated pose into <paramref name="pose"/>.
        /// <para>
        /// Per-joint priority controls which track "wins" when multiple tracks drive the
        /// same joint: a higher <see cref="JointPose.Priority"/> value takes precedence.
        /// Equal-priority joints from different tracks are blended by
        /// <see cref="JointPose.EaseWeight"/>.
        /// </para>
        /// </summary>
        /// <param name="pose">Dictionary keyed by joint name, updated in place.</param>
        public void EvaluatePose(Dictionary<string, JointPose> pose)
        {
            if (Data == null) return;
            float t = CurrentTime;
            float ease = EaseWeight;
            if (ease <= 0f) return;

            foreach (var joint in Data.joints)
            {
                Quaternion? rot = joint.rotationkeys.Length > 0
                    ? InterpolateRotation(joint.rotationkeys, t)
                    : (Quaternion?)null;

                Vector3? pos = joint.positionkeys.Length > 0
                    ? InterpolatePosition(joint.positionkeys, t)
                    : (Vector3?)null;

                if (rot == null && pos == null) continue;

                pose.TryGetValue(joint.Name, out var existing);
                pose[joint.Name] = JointPose.Merge(existing, new JointPose
                {
                    Rotation    = rot ?? Quaternion.Identity,
                    HasRotation = rot.HasValue,
                    Position    = pos ?? Vector3.Zero,
                    HasPosition = pos.HasValue,
                    Priority    = joint.Priority,
                    EaseWeight  = ease,
                });
            }
        }

        // ── Keyframe interpolation ─────────────────────────────────────────────

        private static Quaternion InterpolateRotation(binBVHJointKey[] keys, float t)
        {
            if (keys.Length == 1)
                return DecodeRotation(keys[0].key_element);

            int hi = BracketTime(keys, t);
            if (hi == 0)               return DecodeRotation(keys[0].key_element);
            if (hi >= keys.Length)     return DecodeRotation(keys[keys.Length - 1].key_element);

            float t0 = keys[hi - 1].time, t1 = keys[hi].time;
            float frac = (t1 > t0) ? (t - t0) / (t1 - t0) : 0f;
            return Quaternion.Slerp(
                DecodeRotation(keys[hi - 1].key_element),
                DecodeRotation(keys[hi].key_element),
                frac);
        }

        private static Vector3 InterpolatePosition(binBVHJointKey[] keys, float t)
        {
            if (keys.Length == 1) return keys[0].key_element;

            int hi = BracketTime(keys, t);
            if (hi == 0)               return keys[0].key_element;
            if (hi >= keys.Length)     return keys[keys.Length - 1].key_element;

            float t0 = keys[hi - 1].time, t1 = keys[hi].time;
            float frac = (t1 > t0) ? (t - t0) / (t1 - t0) : 0f;
            return Vector3.Lerp(keys[hi - 1].key_element, keys[hi].key_element, frac);
        }

        // Returns the index of the first key with time >= t (or keys.Length if all are earlier).
        private static int BracketTime(binBVHJointKey[] keys, float t)
        {
            int lo = 0, hi = keys.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (keys[mid].time < t) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        // ── Quaternion decoding ────────────────────────────────────────────────

        /// <summary>
        /// Decodes the compressed quaternion stored in BVH rotation keyframes.
        /// The binary format packs x, y, z into the range [-1, 1]; w is recovered as
        /// sqrt(1 – |xyz|²), which is always ≥ 0 in SL's sign convention.
        /// </summary>
        internal static Quaternion DecodeRotation(Vector3 v)
        {
            float wSq = 1f - v.X * v.X - v.Y * v.Y - v.Z * v.Z;
            float w = wSq > 0f ? (float)Math.Sqrt(wSq) : 0f;
            // Normalize to guard against quantization error.
            var q = new Quaternion(v.X, v.Y, v.Z, w);
            return Quaternion.Normalize(q);
        }
    }
}
