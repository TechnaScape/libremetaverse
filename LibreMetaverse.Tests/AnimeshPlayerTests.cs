using System.Collections.Generic;
using NUnit.Framework;
using LibreMetaverse.Animesh;

namespace LibreMetaverse.Tests
{
    [TestFixture]
    [Category("Animesh")]
    public class AnimeshPlayerTests
    {
        // ── Track management ──────────────────────────────────────────────────

        [Test]
        public void GetOrAddTrack_CreatesNewTrack()
        {
            var player = new AnimeshPlayer(UUID.Random());
            var id = UUID.Random();
            var track = player.GetOrAddTrack(id);
            Assert.That(track, Is.Not.Null);
            Assert.That(track.AnimationID, Is.EqualTo(id));
        }

        [Test]
        public void GetOrAddTrack_SameId_ReturnsSameTrack()
        {
            var player = new AnimeshPlayer(UUID.Random());
            var id = UUID.Random();
            var t1 = player.GetOrAddTrack(id);
            var t2 = player.GetOrAddTrack(id);
            Assert.That(t2, Is.SameAs(t1), "Second call must return the existing track");
        }

        [Test]
        public void GetOrAddTrack_DifferentIds_ReturnDifferentTracks()
        {
            var player = new AnimeshPlayer(UUID.Random());
            var a = player.GetOrAddTrack(UUID.Random());
            var b = player.GetOrAddTrack(UUID.Random());
            Assert.That(b, Is.Not.SameAs(a));
        }

        [Test]
        public void TrackCount_IncreasesWithNewTracks()
        {
            var player = new AnimeshPlayer(UUID.Random());
            player.GetOrAddTrack(UUID.Random());
            player.GetOrAddTrack(UUID.Random());
            Assert.That(player.TrackCount, Is.EqualTo(2));
        }

        [Test]
        public void TrackCount_StableForDuplicateId()
        {
            var player = new AnimeshPlayer(UUID.Random());
            var id = UUID.Random();
            player.GetOrAddTrack(id);
            player.GetOrAddTrack(id);
            Assert.That(player.TrackCount, Is.EqualTo(1));
        }

        [Test]
        public void RetainOnly_RemovesAbsentIds()
        {
            var player = new AnimeshPlayer(UUID.Random());
            var keep = UUID.Random();
            var drop = UUID.Random();
            player.GetOrAddTrack(keep);
            player.GetOrAddTrack(drop);

            player.RetainOnly(new HashSet<UUID> { keep });

            Assert.That(player.TrackCount, Is.EqualTo(1));
        }

        [Test]
        public void RetainOnly_KeepsPresentIds()
        {
            var player = new AnimeshPlayer(UUID.Random());
            var id = UUID.Random();
            var original = player.GetOrAddTrack(id);

            player.RetainOnly(new HashSet<UUID> { id });

            var still = player.GetOrAddTrack(id); // should return the same object
            Assert.That(still, Is.SameAs(original));
        }

        [Test]
        public void RetainOnly_EmptySet_RemovesAllTracks()
        {
            var player = new AnimeshPlayer(UUID.Random());
            player.GetOrAddTrack(UUID.Random());
            player.GetOrAddTrack(UUID.Random());

            player.RetainOnly(new HashSet<UUID>());

            Assert.That(player.TrackCount, Is.EqualTo(0));
        }

        [Test]
        public void DiagnosticSnapshotsPreserveMergeOrderAndDoNotAdvanceThePlayer()
        {
            var player = new AnimeshPlayer(UUID.Random());
            var first = player.GetOrAddTrack(UUID.Random(), 12);
            first.Data = MakeAnimation(outPoint: 5f);
            first.Advance(2f);
            var pending = player.GetOrAddTrack(UUID.Random(), 13);

            var snapshots = player.SnapshotTracks();
            Assert.That(snapshots[0].AnimationID, Is.EqualTo(first.AnimationID));
            Assert.That(snapshots[1].AnimationID, Is.EqualTo(pending.AnimationID));
            Assert.That(snapshots[1].Data, Is.Null);
            Assert.That(snapshots[0].Sequence, Is.EqualTo(12));
            Assert.That(snapshots[0].CurrentTime, Is.EqualTo(2f));
            snapshots[0].Advance(9f);
            Assert.That(snapshots[0].IsFinished, Is.True);
            Assert.That(first.CurrentTime, Is.EqualTo(2f));
            Assert.That(first.IsFinished, Is.False);
            player.Update(1f);
            Assert.That(first.CurrentTime, Is.EqualTo(3f));
            Assert.That(snapshots[0].CurrentTime, Is.EqualTo(5f));
        }

        [Test]
        public void NewSequence_RestartsFinishedTrackWithoutDiscardingAsset()
        {
            var player = new AnimeshPlayer(UUID.Random());
            var id = UUID.Random();
            var original = player.GetOrAddTrack(id, 10);
            original.Data = MakeAnimation(outPoint: 1f);
            original.Advance(2f);
            Assert.That(original.IsFinished, Is.True);

            var restarted = player.GetOrAddTrack(id, 11);

            Assert.That(restarted, Is.SameAs(original));
            Assert.That(restarted.Data, Is.Not.Null);
            Assert.That(restarted.CurrentTime, Is.Zero);
            Assert.That(restarted.IsFinished, Is.False);
            Assert.That(restarted.SequenceChanges, Is.EqualTo(1));
            Assert.That(restarted.Restarts, Is.EqualTo(1));
        }

        [Test]
        public void RepeatedSequence_DoesNotRestartPlayback()
        {
            var player = new AnimeshPlayer(UUID.Random());
            var id = UUID.Random();
            var track = player.GetOrAddTrack(id, 10);
            track.Data = MakeAnimation(outPoint: 5f);
            track.Advance(2f);

            player.GetOrAddTrack(id, 10);

            Assert.That(track.CurrentTime, Is.EqualTo(2f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NewSequence_DoesNotJumpAnActiveAnimationBackToItsFirstFrame(bool loop)
        {
            var player = new AnimeshPlayer(UUID.Random());
            var id = UUID.Random();
            var track = player.GetOrAddTrack(id, 10);
            track.Data = MakeAnimation(outPoint: 5f, loop: loop);
            player.Update(2f);

            // Scripts routinely re-signal an already active motion with a new sequence.
            // Firestorm's motion controller lets it continue instead of resetting its clock.
            player.GetOrAddTrack(id, 11);
            player.Update(.016f);

            Assert.That(track.Sequence, Is.EqualTo(11));
            Assert.That(track.CurrentTime, Is.EqualTo(2.016f).Within(1e-5));
            Assert.That(track.ElapsedTime, Is.EqualTo(2.016f).Within(1e-5));
            Assert.That(track.SequenceChanges, Is.EqualTo(1));
            Assert.That(track.Restarts, Is.Zero);
        }

        [Test]
        public void CompleteStateKeepsLoadedTrackAndRequestsOnlyNewNonzeroAssets()
        {
            var player = new AnimeshPlayer(UUID.Random());
            var loaded = player.GetOrAddTrack(UUID.Random(), 10);
            loaded.Data = MakeAnimation(5f, true);
            loaded.Advance(2f);
            var removed = player.GetOrAddTrack(UUID.Random(), 10);
            var added = UUID.Random();

            var pending = player.ApplyAnimations(new[]
            {
                new Animation { AnimationID = loaded.AnimationID, AnimationSequence = 11 },
                new Animation { AnimationID = added, AnimationSequence = 12 },
                new Animation { AnimationID = added, AnimationSequence = 12 },
                new Animation { AnimationID = UUID.Zero },
            });

            Assert.That(player.TrackCount, Is.EqualTo(2));
            Assert.That(pending.Length, Is.EqualTo(1));
            Assert.That(pending[0].AnimationID, Is.EqualTo(added));
            Assert.That(loaded.CurrentTime, Is.EqualTo(2f));
            Assert.That(player.TryApplyData(removed.AnimationID, MakeAnimation(5f)), Is.False);
        }

        [Test]
        public void CompleteStateNeverExposesAnIntermediateEmptyPoseToTheRenderThread()
        {
            var player = new AnimeshPlayer(UUID.Random());
            var states = new[]
            {
                new[] { new Animation { AnimationID = UUID.Random(), AnimationSequence = 1 } },
                new[] { new Animation { AnimationID = UUID.Random(), AnimationSequence = 2 } },
            };
            player.ApplyAnimations(states[0]);
            var writer = System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < 4000; i++) player.ApplyAnimations(states[i % 2]);
            });

            for (int i = 0; i < 4000; i++)
                Assert.That(player.SnapshotTracks().Length, Is.EqualTo(1));
            Assert.That(writer.Wait(System.TimeSpan.FromSeconds(10)), Is.True);
        }

        [Test]
        public void FrameStallDiagnosticsDoNotSlowDownOrReplayTheAnimationClock()
        {
            var player = new AnimeshPlayer(UUID.Random());
            var track = player.GetOrAddTrack(UUID.Random());
            track.Data = MakeAnimation(5f, true);
            player.Update(.016f);
            player.Update(.4f);
            player.Update(.016f);
            player.Update(float.NaN);
            player.Update(float.PositiveInfinity);

            Assert.That(track.ElapsedTime, Is.EqualTo(.432f).Within(1e-5));
            Assert.That(player.LastFrameSeconds, Is.EqualTo(.016f));
            Assert.That(player.MaximumFrameSeconds, Is.EqualTo(.4f));
            Assert.That(player.FramesOver100Milliseconds, Is.EqualTo(1));
        }

        [Test]
        public void LateAssetDoesNotResurrectStoppedAnimation()
        {
            var player = new AnimeshPlayer(UUID.Random());
            var id = UUID.Random();
            player.GetOrAddTrack(id, 10);
            player.RetainOnly(new HashSet<UUID>());

            bool applied = player.TryApplyData(id, MakeAnimation(outPoint: 1f));

            Assert.That(applied, Is.False);
            Assert.That(player.TrackCount, Is.Zero);
        }

        // ── Playback ──────────────────────────────────────────────────────────

        [Test]
        public void Update_WithNoTracks_DoesNotThrow()
        {
            var player = new AnimeshPlayer(UUID.Random());
            Assert.DoesNotThrow(() => player.Update(0.016f));
        }

        [Test]
        public void Update_WithTracksButNoData_DoesNotThrow()
        {
            var player = new AnimeshPlayer(UUID.Random());
            player.GetOrAddTrack(UUID.Random());
            player.GetOrAddTrack(UUID.Random());
            Assert.DoesNotThrow(() => player.Update(0.016f));
        }

        [Test]
        public void EvaluatePose_NoLoadedData_ReturnsEmptyDictionary()
        {
            var player = new AnimeshPlayer(UUID.Random());
            player.GetOrAddTrack(UUID.Random()); // track exists but Data == null
            var pose = player.EvaluatePose();
            Assert.That(pose, Is.Empty);
        }

        [Test]
        public void EvaluatePoseInto_ReusesAndClearsCallerDictionary()
        {
            var player = new AnimeshPlayer(UUID.Random());
            var pose = new Dictionary<string, JointPose>
            {
                ["stale"] = default,
            };

            player.EvaluatePose(pose);

            Assert.That(pose, Is.Empty);
        }

        // ── ObjectID ─────────────────────────────────────────────────────────

        [Test]
        public void ObjectID_StoresConstructorValue()
        {
            var id = UUID.Random();
            var player = new AnimeshPlayer(id);
            Assert.That(player.ObjectID, Is.EqualTo(id));
        }

        private static BinBVHAnimationReader MakeAnimation(float outPoint, bool loop = false)
        {
            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);
            writer.Write((ushort)1);
            writer.Write((ushort)0);
            writer.Write(2);
            writer.Write(outPoint);
            writer.Write((byte)0);
            writer.Write(0f);
            writer.Write(outPoint);
            writer.Write(loop ? 1 : 0);
            writer.Write(0f);
            writer.Write(0f);
            writer.Write((uint)0);
            writer.Write((uint)0);
            writer.Flush();
            return new BinBVHAnimationReader(ms.ToArray());
        }
    }
}
