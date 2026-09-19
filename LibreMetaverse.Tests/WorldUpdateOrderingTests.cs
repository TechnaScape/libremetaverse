using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using LibreMetaverse.Packets;
using NUnit.Framework;

namespace LibreMetaverse.Tests
{
    [TestFixture]
    public class WorldUpdateOrderingTests
    {
        [Test]
        public void PrimEditsArePublishedBeforeNextPacketMutatesTheSharedPrimitive()
        {
            using var client = new GridClient();
            using var sim = new Simulator(client, new IPEndPoint(IPAddress.Loopback, 13), 0);
            client.Settings.World.TrackObjects = true;
            var scales = new List<float>();
            var threads = new List<int>();
            client.Objects.ObjectUpdate += (_, e) => {
                scales.Add(e.Prim.Scale.X);
                threads.Add(Environment.CurrentManagedThreadId);
            };
            var id = UUID.Random();
            foreach (float scale in new[] { 1f, 2f })
            {
                var packet = new ObjectUpdatePacket { ObjectData = new[] {
                    new ObjectUpdatePacket.ObjectDataBlock {
                        ID = 42, FullID = id, PCode = (byte)PCode.Prim,
                        Scale = new Vector3(scale), ObjectData = new byte[60],
                        TextureEntry = new Primitive.TextureEntry(UUID.Random()).GetBytes(),
                        TextColor = new byte[4], ExtraParams = new byte[1],
                        NameValue = Array.Empty<byte>(), TextureAnim = Array.Empty<byte>(),
                        PSBlock = Array.Empty<byte>(), Data = Array.Empty<byte>(),
                        MediaURL = Array.Empty<byte>(), Text = Array.Empty<byte>(),
                    }
                }};
                client.Network.PacketEvents.InvokeRaiseEvent(PacketType.ObjectUpdate, packet, sim);
                Assert.That(scales.Count, Is.EqualTo((int)scale), "the edit must be copied before processing another packet");
            }
            Assert.That(scales, Is.EqualTo(new[] { 1f, 2f }));
            Assert.That(threads, Is.All.EqualTo(Environment.CurrentManagedThreadId));
        }

        [Test]
        public void AvatarAppearanceEditsReachConsumerInPacketOrder()
        {
            using var client = new GridClient();
            using var sim = new Simulator(client, new IPEndPoint(IPAddress.Loopback, 13), 0);
            var bakes = new List<UUID>();
            client.Avatars.AvatarAppearance += (_, e) => bakes.Add(e.DefaultTexture.TextureID);
            var avatar = UUID.Random();
            var first = UUID.Random();
            var second = UUID.Random();
            foreach (UUID bake in new[] { first, second })
            {
                var packet = Appearance(avatar, bake, visualParams: 2, cofVersion: 0);
                client.Network.PacketEvents.InvokeRaiseEvent(PacketType.AvatarAppearance, packet, sim);
            }
            Assert.That(bakes, Is.EqualTo(new[] { first, second }));
        }

        /// <summary>
        /// [SLUnity] An appearance with one visual parameter is the simulator's placeholder, not a
        /// bake: the SL viewer ignores it before recording its COF version
        /// (LLVOAvatar::processAvatarAppearance, "ignoring appearance message due to lack of
        /// params"). Recorded, it made the login appearance pass skip UpdateAvatarAppearance and our
        /// own avatar kept the placeholder's empty bakes -- no skin -- until an outfit change.
        /// </summary>
        [Test]
        public void APlaceholderAppearanceForSelfIsIgnoredAndLeavesTheCofVersionAlone()
        {
            using var client = new GridClient();
            using var sim = new Simulator(client, new IPEndPoint(IPAddress.Loopback, 13), 0);
            var raised = 0;
            client.Avatars.AvatarAppearance += (_, e) => raised++;

            var placeholder = Appearance(client.Self.AgentID, UUID.Zero, visualParams: 1, cofVersion: 20024);
            client.Network.PacketEvents.InvokeRaiseEvent(PacketType.AvatarAppearance, placeholder, sim);

            Assert.That(raised, Is.Zero, "a one-parameter appearance is not published");
            Assert.That(client.Appearance.LastUpdateReceivedCOFVersion, Is.EqualTo(-1),
                "its COF version must not stop the login bake request");

            var baked = Appearance(client.Self.AgentID, UUID.Random(), visualParams: 253, cofVersion: 20077);
            client.Network.PacketEvents.InvokeRaiseEvent(PacketType.AvatarAppearance, baked, sim);

            Assert.That(raised, Is.EqualTo(1), "a full appearance is published");
            Assert.That(client.Appearance.LastUpdateReceivedCOFVersion, Is.EqualTo(20077));
        }

        [Test]
        public void APlaceholderAppearanceForSomebodyElseIsNotPublished()
        {
            using var client = new GridClient();
            using var sim = new Simulator(client, new IPEndPoint(IPAddress.Loopback, 13), 0);
            var raised = 0;
            client.Avatars.AvatarAppearance += (_, e) => raised++;

            var packet = Appearance(UUID.Random(), UUID.Zero, visualParams: 0, cofVersion: 5);
            client.Network.PacketEvents.InvokeRaiseEvent(PacketType.AvatarAppearance, packet, sim);

            Assert.That(raised, Is.Zero);
        }

        private static AvatarAppearancePacket Appearance(UUID sender, UUID bake, int visualParams, int cofVersion)
        {
            var packet = new AvatarAppearancePacket();
            packet.Sender.ID = sender;
            packet.ObjectData.TextureEntry = new Primitive.TextureEntry(bake).GetBytes();
            packet.VisualParam = Enumerable.Range(0, visualParams)
                .Select(_ => new AvatarAppearancePacket.VisualParamBlock { ParamValue = 128 })
                .ToArray();
            packet.AppearanceData = new[]
            {
                new AvatarAppearancePacket.AppearanceDataBlock
                {
                    AppearanceVersion = 1, CofVersion = cofVersion, Flags = 0,
                },
            };
            return packet;
        }
    }
}
