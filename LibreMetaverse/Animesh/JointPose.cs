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

namespace LibreMetaverse.Animesh
{
    /// <summary>
    /// The animation-driven override for a single skeleton joint at a moment in time.
    /// Produced by <see cref="AnimationTrack.EvaluatePose"/> and consumed by
    /// <see cref="AnimeshSkinning"/>.
    /// </summary>
    public struct JointPose
    {
        /// <summary>Local rotation from the animation.  Valid only when <see cref="HasRotation"/> is true.</summary>
        public Quaternion Rotation;

        /// <summary>True when the animation provides a rotation key for this joint.</summary>
        public bool HasRotation;

        /// <summary>
        /// Local position from the animation. Animesh can reposition any joint.
        /// Valid only when <see cref="HasPosition"/> is true.
        /// </summary>
        public Vector3 Position;

        /// <summary>True when the animation provides a position key for this joint.</summary>
        public bool HasPosition;

        /// <summary>
        /// Per-joint priority from the source <see cref="binBVHJoint"/>.
        /// Higher values override lower ones during pose blending.
        /// </summary>
        public int Priority;

        /// <summary>
        /// Ease-in/ease-out weight in [0, 1].  Used to blend equal-priority joints smoothly
        /// when an animation starts or ends.
        /// </summary>
        public float EaseWeight;

        private bool _hasChannelWeights;
        private int _rotationPriority, _positionPriority;
        private float _rotationWeight, _positionWeight;

        /// <summary>Priority of the rotation contributors, independent of position.</summary>
        public int RotationPriority => _hasChannelWeights ? _rotationPriority : Priority;
        /// <summary>Priority of the position contributors, independent of rotation.</summary>
        public int PositionPriority => _hasChannelWeights ? _positionPriority : Priority;
        /// <summary>Sum of rotation contributors' weights, for subsequent merges.</summary>
        public float RotationWeight => _hasChannelWeights ? _rotationWeight : EaseWeight;
        /// <summary>Sum of position contributors' weights, for subsequent merges.</summary>
        public float PositionWeight => _hasChannelWeights ? _positionWeight : EaseWeight;

        /// <summary>
        /// Combines channels independently. A rotation-only animation must not discard a
        /// lower-priority position on the same joint. Keep the sums (not a clamped weight)
        /// so three or more equal-priority contributors retain their relative influence.
        /// This also applies when merging already-combined poses from linked objects.
        /// </summary>
        public static JointPose Merge(JointPose existing, JointPose candidate)
        {
            int rp = existing.RotationPriority, pp = existing.PositionPriority;
            float rw = existing.HasRotation ? existing.RotationWeight : 0f;
            float pw = existing.HasPosition ? existing.PositionWeight : 0f;
            if (candidate.HasRotation && candidate.RotationWeight > 0f)
            {
                if (!existing.HasRotation || candidate.RotationPriority > rp)
                {
                    existing.Rotation = candidate.Rotation;
                    rp = candidate.RotationPriority;
                    rw = candidate.RotationWeight;
                    existing.HasRotation = true;
                }
                else if (candidate.RotationPriority == rp)
                {
                    rw += candidate.RotationWeight;
                    existing.Rotation = Quaternion.Slerp(existing.Rotation, candidate.Rotation,
                        candidate.RotationWeight / rw);
                }
            }
            if (candidate.HasPosition && candidate.PositionWeight > 0f)
            {
                if (!existing.HasPosition || candidate.PositionPriority > pp)
                {
                    existing.Position = candidate.Position;
                    pp = candidate.PositionPriority;
                    pw = candidate.PositionWeight;
                    existing.HasPosition = true;
                }
                else if (candidate.PositionPriority == pp)
                {
                    pw += candidate.PositionWeight;
                    existing.Position = Vector3.Lerp(existing.Position, candidate.Position,
                        candidate.PositionWeight / pw);
                }
            }
            existing._hasChannelWeights = true;
            existing._rotationPriority = rp;
            existing._positionPriority = pp;
            existing._rotationWeight = rw;
            existing._positionWeight = pw;
            existing.Priority = existing.HasRotation && existing.HasPosition
                ? System.Math.Max(rp, pp) : existing.HasRotation ? rp : pp;
            existing.EaseWeight = System.Math.Min(1f, System.Math.Max(rw, pw));
            return existing;
        }
    }
}
