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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LibreMetaverse.Animesh
{
    /// <summary>
    /// GridClient subsystem that tracks which animations are playing on Animesh objects
    /// and manages the per-object <see cref="AnimeshPlayer"/> instances.
    /// <para>
    /// The manager listens to <see cref="ObjectManager.ObjectAnimation"/> events, requests
    /// animation assets from the asset server, and feeds parsed data into the relevant
    /// <see cref="AnimationTrack"/>.  The host application drives playback by calling
    /// <see cref="Update"/> once per frame and then calling
    /// <see cref="AnimeshPlayer.EvaluatePose"/> on individual players.
    /// </para>
    /// </summary>
    public sealed class AnimeshManager
    {
        private readonly GridClient _client;

        // One player per in-world object UUID.
        private readonly ConcurrentDictionary<UUID, AnimeshPlayer> _players
            = new ConcurrentDictionary<UUID, AnimeshPlayer>();

        // ObjectAnimation state is commonly repeated while an animation asset is still in flight.
        // Without this guard every repeat opened another HTTP/UDP request and decoded the same BVH
        // again, producing both load spikes and nondeterministic late completion order.
        private readonly ConcurrentDictionary<AnimationRequest, byte> _fetching
            = new ConcurrentDictionary<AnimationRequest, byte>();

        private readonly struct AnimationRequest : IEquatable<AnimationRequest>
        {
            public readonly UUID ObjectID;
            public readonly UUID AnimationID;

            public AnimationRequest(UUID objectID, UUID animationID)
            {
                ObjectID = objectID;
                AnimationID = animationID;
            }

            public bool Equals(AnimationRequest other)
                => ObjectID == other.ObjectID && AnimationID == other.AnimationID;

            public override bool Equals(object? obj)
                => obj is AnimationRequest other && Equals(other);

            public override int GetHashCode()
                => HashCode.Combine(ObjectID, AnimationID);
        }

        internal AnimeshManager(GridClient client)
        {
            _client = client;
            _client.Objects.ObjectAnimation += OnObjectAnimation;
            _client.Objects.KillObject += OnKillObject;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the <see cref="AnimeshPlayer"/> for <paramref name="objectID"/>,
        /// or null if the object has never received an ObjectAnimation event.
        /// </summary>
        public AnimeshPlayer? GetPlayer(UUID objectID)
            => _players.TryGetValue(objectID, out var p) ? p : null;

        /// <summary>
        /// Returns a snapshot of all currently tracked players.
        /// </summary>
        public IEnumerable<AnimeshPlayer> AllPlayers => _players.Values;

        /// <summary>
        /// Advance all players by <paramref name="dt"/> seconds.
        /// Call once per frame, typically from a fixed-rate simulation or render loop.
        /// </summary>
        public void Update(float dt)
        {
            // Values copies the ConcurrentDictionary to a collection and takes all its locks.
            // Enumerating pairs is safe during packet arrivals and avoids that per-frame copy.
            foreach (var player in _players)
                player.Value.Update(dt);
        }

        /// <summary>
        /// Remove the player for an object that has left the scene.
        /// </summary>
        public void RemovePlayer(UUID objectID) => _players.TryRemove(objectID, out _);

        /// <summary>Drops playback state before ObjectManager forgets the killed primitive.</summary>
        private void OnKillObject(object? sender, KillObjectEventArgs e)
        {
            if (e?.Simulator == null) return;

            if (e.Simulator.ObjectsPrimitives.TryGetValue(e.ObjectLocalID, out Primitive? prim)
                && prim != null && prim.ID != UUID.Zero)
            {
                RemovePlayer(prim.ID);
            }
        }

        // ── ObjectAnimation handler ───────────────────────────────────────────

        private void OnObjectAnimation(object? sender, ObjectAnimationEventArgs e)
        {
            var player = _players.GetOrAdd(e.ObjectID, id => new AnimeshPlayer(id));

            foreach (AnimationTrack track in player.ApplyAnimations(e.Animations))
            {
                // Request the animation asset; decode it when it arrives.
                UUID animID = track.AnimationID;
                var request = new AnimationRequest(e.ObjectID, animID);
                if (_fetching.TryAdd(request, 0))
                    _ = FetchAndApplyAsync(player, animID, request);
            }
        }
        // ── Asset fetching ────────────────────────────────────────────────────

        private async Task FetchAndApplyAsync(AnimeshPlayer player, UUID animID,
            AnimationRequest request)
        {
            try
            {
                var asset = await _client.Assets.RequestAssetAsync(animID, AssetType.Animation, false)
                    .ConfigureAwait(false);

                if (asset?.AssetData == null) return;

                var decoded = new BinBVHAnimationReader(asset.AssetData);

                // The player may already have discarded this track if the server stopped the
                // animation before the download finished. Never recreate it from a stale request.
                player.TryApplyData(animID, decoded);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[AnimeshManager] Failed to fetch animation {animID}: {ex.Message}");
            }
            finally
            {
                _fetching.TryRemove(request, out _);
            }
        }
    }
}
