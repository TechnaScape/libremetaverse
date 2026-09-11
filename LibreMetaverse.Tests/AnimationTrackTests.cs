using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using LibreMetaverse.Animesh;

namespace LibreMetaverse.Tests
{
    [TestFixture]
    [Category("Animesh")]
    public class AnimationTrackTests
    {
        [Test]
        public void WirePositionKeysUseSymmetricMetresAndTimesUseFullDuration()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((ushort)1);
            writer.Write((ushort)0);
            writer.Write(4);
            writer.Write(10f);
            writer.Write((byte)0);
            writer.Write(2f);
            writer.Write(8f);
            writer.Write(1);
            writer.Write(0f);
            writer.Write(0f);
            writer.Write(0u);
            writer.Write(1u);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("mTail2\0"));
            writer.Write(4);
            writer.Write(2);
            foreach (ushort time in new ushort[] { 0, 65535 })
            {
                writer.Write(time);
                writer.Write((ushort)32767);
                writer.Write((ushort)32767);
                writer.Write((ushort)32767);
            }
            writer.Write(1);
            writer.Write((ushort)32767);
            writer.Write((ushort)0);
            writer.Write((ushort)32767);
            writer.Write((ushort)65535);
            writer.Flush();

            var decoded = new BinBVHAnimationReader(stream.ToArray());
            var joint = decoded.joints[0];
            Assert.That(joint.rotationkeys[0].time, Is.Zero);
            Assert.That(joint.rotationkeys[1].time, Is.EqualTo(10f));
            Assert.That(joint.positionkeys[0].time, Is.EqualTo(5f).Within(.001f));
            Assert.That(joint.positionkeys[0].key_element.X, Is.EqualTo(-5f));
            Assert.That(joint.positionkeys[0].key_element.Y, Is.EqualTo(0f).Within(.0002f),
                "a zero offset must not add half a metre to every Animesh joint");
            Assert.That(joint.positionkeys[0].key_element.Z, Is.EqualTo(5f));

            var track = new AnimationTrack(UUID.Random()) { Data = decoded };
            var pose = new Dictionary<string, JointPose>();
            track.Advance(5f);
            track.EvaluatePose(pose);
            Assert.That(pose["mTail2"].Position.Y, Is.EqualTo(0f).Within(.0002f));
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        // Reduced from the Pazuta F8 capture: the base pose repositions mNeck, while
        // a higher-priority track has only rotation keys for the same joint.
        [TestCase(false)]
        [TestCase(true)]
        public void RotationOnlyTrackPreservesLowerPriorityPosition(bool reverse)
        {
            var position = PoseTrack(2, new Vector3(.06691074f, 0f, -.2048521f), null);
            var rotation = PoseTrack(3, null, new Vector3(0f, .08302438f, 0f));
            var pose = new Dictionary<string, JointPose>();
            (reverse ? rotation : position).EvaluatePose(pose);
            (reverse ? position : rotation).EvaluatePose(pose);
            Assert.That(pose["mNeck"].HasPosition, Is.True);
            Assert.That(pose["mNeck"].HasRotation, Is.True);
            Assert.That(pose["mNeck"].Position.Z, Is.EqualTo(-.2048521f).Within(1e-6f));
            Assert.That(pose["mNeck"].Rotation.Y, Is.EqualTo(.08302438f).Within(1e-6f));
        }

        [Test]
        public void EqualPriorityChannelsAverageAllContributorsWithoutWeightFromOtherChannel()
        {
            var pose = new Dictionary<string, JointPose>();
            PoseTrack(2, new Vector3(1, 0, 0), null).EvaluatePose(pose);
            PoseTrack(2, null, Vector3.Zero).EvaluatePose(pose);
            PoseTrack(2, new Vector3(3, 0, 0), null).EvaluatePose(pose);
            PoseTrack(2, new Vector3(5, 0, 0), null).EvaluatePose(pose);
            Assert.That(pose["mNeck"].Position.X, Is.EqualTo(3f).Within(1e-6f));
        }

        [Test]
        public void ZeroDurationLoopHoldsPoseAfterEaseIn()
        {
            var track = MakeTrack(outPoint: 0f, loop: true, easeIn: .8f, easeOut: .8f);
            track.Advance(.4f);
            Assert.That(track.EaseWeight, Is.EqualTo(.5f).Within(1e-6f));
            track.Advance(43.67378f);
            Assert.That(track.IsFinished, Is.False);
            Assert.That(track.EaseWeight, Is.EqualTo(1f));
        }

        [Test]
        public void LoopDoesNotEaseOutOrRestartEaseInAtEveryWrap()
        {
            var track = MakeTrack(outPoint: 1f, loop: true, easeIn: .4f, easeOut: .4f);
            track.Advance(.9f);
            Assert.That(track.EaseWeight, Is.EqualTo(1f));
            track.Advance(.2f);
            Assert.That(track.CurrentTime, Is.EqualTo(.1f).Within(1e-5f));
            Assert.That(track.EaseWeight, Is.EqualTo(1f));
        }

        [Test]
        public void NonLoopingPlaybackUsesDurationNotLoopWindow()
        {
            var track = MakeTrack(inPoint: 2f, outPoint: 8f, easeOut: 1f);
            track.Data!.Length = 10f;
            track.Advance(8.5f);
            Assert.That(track.IsFinished, Is.False);
            Assert.That(track.EaseWeight, Is.EqualTo(1f));
            track.Advance(2f);
            Assert.That(track.CurrentTime, Is.EqualTo(10f));
            Assert.That(track.IsFinished, Is.True);
        }

        private static AnimationTrack PoseTrack(int priority, Vector3? position, Vector3? rotation)
        {
            var track = MakeTrack(loop: true);
            track.Data!.joints = new[] { new binBVHJoint {
                Name = "mNeck", Priority = priority,
                positionkeys = position.HasValue
                    ? new[] { new binBVHJointKey { time = 0, key_element = position.Value } }
                    : System.Array.Empty<binBVHJointKey>(),
                rotationkeys = rotation.HasValue
                    ? new[] { new binBVHJointKey { time = 0, key_element = rotation.Value } }
                    : System.Array.Empty<binBVHJointKey>(),
            }};
            return track;
        }

        /// <summary>
        /// Builds a minimal valid BVH binary blob with no joints, so we can construct a
        /// <see cref="BinBVHAnimationReader"/> with controlled InPoint/OutPoint/Loop/EaseIn/EaseOut.
        /// </summary>
        private static BinBVHAnimationReader MakeAnimation(
            float inPoint, float outPoint, bool loop,
            float easeIn = 0f, float easeOut = 0f)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((ushort)1);            // unknown0
            bw.Write((ushort)0);            // unknown1
            bw.Write((int)2);               // Priority
            bw.Write(outPoint - inPoint);   // Length
            bw.Write((byte)0);              // ExpressionName = "" (null-terminated)
            bw.Write(inPoint);              // InPoint
            bw.Write(outPoint);             // OutPoint
            bw.Write(loop ? 1 : 0);         // Loop (stored as int)
            bw.Write(easeIn);               // EaseInTime
            bw.Write(easeOut);              // EaseOutTime
            bw.Write((uint)0);              // HandPose
            bw.Write((uint)0);              // JointCount = 0
            bw.Flush();
            return new BinBVHAnimationReader(ms.ToArray());
        }

        private static AnimationTrack MakeTrack(
            float inPoint = 0f, float outPoint = 10f, bool loop = false,
            float easeIn = 0f, float easeOut = 0f)
        {
            var track = new AnimationTrack(UUID.Random());
            track.Data = MakeAnimation(inPoint, outPoint, loop, easeIn, easeOut);
            return track;
        }

        // ── DecodeRotation ─────────────────────────────────────────────────────

        [Test]
        public void DecodeRotation_ZeroVector_ReturnsIdentity()
        {
            var q = AnimationTrack.DecodeRotation(Vector3.Zero);
            Assert.That(q.W, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(q.X, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(q.Y, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(q.Z, Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void DecodeRotation_Result_IsNormalized()
        {
            var testVectors = new[]
            {
                new Vector3(0.1f, 0.2f, 0.3f),
                new Vector3(0.5f, 0f,   0f),
                new Vector3(0f,   0f,   0.99f),
                Vector3.Zero,
            };

            foreach (var v in testVectors)
            {
                var q = AnimationTrack.DecodeRotation(v);
                float magnitude = (float)System.Math.Sqrt(
                    q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
                Assert.That(magnitude, Is.EqualTo(1f).Within(1e-5f),
                    $"Quaternion for {v} should be unit length");
            }
        }

        [Test]
        public void DecodeRotation_WIsRecoveredFromXyz()
        {
            // x=0.6, y=0, z=0  →  wSq = 1 - 0.36 = 0.64  →  w = 0.8
            var q = AnimationTrack.DecodeRotation(new Vector3(0.6f, 0f, 0f));
            Assert.That(q.X, Is.EqualTo(0.6f).Within(1e-5f));
            Assert.That(q.W, Is.EqualTo(0.8f).Within(1e-5f));
        }

        [Test]
        public void DecodeRotation_WAlwaysNonNegative()
        {
            // SL BVH stores the sign convention where w >= 0 always.
            var testVectors = new[]
            {
                new Vector3(0.5f, 0.5f, 0.5f),
                new Vector3(-0.5f, -0.5f, -0.5f),
                new Vector3(0f, 0f, 0.9f),
            };

            foreach (var v in testVectors)
            {
                var q = AnimationTrack.DecodeRotation(v);
                Assert.That(q.W, Is.GreaterThanOrEqualTo(0f),
                    $"w must be >= 0 for input {v}");
            }
        }

        // ── No-data behaviour ──────────────────────────────────────────────────

        [Test]
        public void Advance_WithNullData_DoesNotChangeCurrentTime()
        {
            var track = new AnimationTrack(UUID.Random());
            track.Advance(1f);
            Assert.That(track.CurrentTime, Is.EqualTo(0f));
            Assert.That(track.IsFinished, Is.False);
        }

        [Test]
        public void EaseWeight_WithNullData_ReturnsZero()
        {
            var track = new AnimationTrack(UUID.Random());
            Assert.That(track.EaseWeight, Is.EqualTo(0f));
        }

        [Test]
        public void EvaluatePose_WithNullData_LeavesEmptyDictionaryUnchanged()
        {
            var track = new AnimationTrack(UUID.Random());
            var pose = new Dictionary<string, JointPose>();
            track.EvaluatePose(pose);
            Assert.That(pose, Is.Empty);
        }

        // ── Advance / timing ───────────────────────────────────────────────────

        [Test]
        public void Advance_NonLooping_BeforeOutPoint_AdvancesTime()
        {
            var track = MakeTrack(outPoint: 10f, loop: false);
            track.Advance(3f);
            Assert.That(track.CurrentTime, Is.EqualTo(3f).Within(1e-5f));
            Assert.That(track.IsFinished, Is.False);
        }

        [Test]
        public void Advance_NonLooping_PastOutPoint_ClampsAndSetsFinished()
        {
            var track = MakeTrack(outPoint: 5f, loop: false);
            track.Advance(10f);
            Assert.That(track.CurrentTime, Is.EqualTo(5f).Within(1e-5f));
            Assert.That(track.IsFinished, Is.True);
        }

        [Test]
        public void Advance_Looping_WrapsAroundToInPoint()
        {
            // InPoint=1, OutPoint=4 → span=3.  After advancing 6s, time should wrap:
            // t=1 + (6-1)%3 = 1 + 5%3 = 1 + 2 = 3.
            var track = MakeTrack(inPoint: 1f, outPoint: 4f, loop: true);
            track.Advance(6f);
            Assert.That(track.CurrentTime, Is.EqualTo(3f).Within(1e-4f));
            Assert.That(track.IsFinished, Is.False);
        }

        [Test]
        public void Advance_Finished_DoesNotAdvanceFurther()
        {
            var track = MakeTrack(outPoint: 5f, loop: false);
            track.Advance(10f);
            float stoppedAt = track.CurrentTime;
            track.Advance(5f);
            Assert.That(track.CurrentTime, Is.EqualTo(stoppedAt));
        }

        // ── EaseWeight ─────────────────────────────────────────────────────────

        [Test]
        public void EaseWeight_InMiddle_IsOne()
        {
            var track = MakeTrack(outPoint: 10f, easeIn: 1f, easeOut: 1f);
            track.Advance(5f);
            Assert.That(track.EaseWeight, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void EaseWeight_DuringEaseIn_IsFractional()
        {
            // easeIn=2s.  At t=1s we expect weight=0.5.
            var track = MakeTrack(outPoint: 10f, easeIn: 2f);
            track.Advance(1f);
            Assert.That(track.EaseWeight, Is.EqualTo(0.5f).Within(1e-5f));
        }

        [Test]
        public void EaseWeight_DuringEaseOut_DecreasesTowardZero()
        {
            // outPoint=10, easeOut=4.  easeOutStart=6.  At t=8 → weight=(10-8)/4 = 0.5.
            var track = MakeTrack(outPoint: 10f, easeOut: 4f);
            track.Advance(8f);
            Assert.That(track.EaseWeight, Is.EqualTo(0.5f).Within(1e-5f));
        }

        [Test]
        public void EaseWeight_NoEaseCurves_IsAlwaysOne()
        {
            var track = MakeTrack(outPoint: 10f);
            track.Advance(5f);
            Assert.That(track.EaseWeight, Is.EqualTo(1f));
        }
    }
}
