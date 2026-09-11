using System;
using System.Collections.Generic;
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
                var packet = new AvatarAppearancePacket();
                packet.Sender.ID = avatar;
                packet.ObjectData.TextureEntry = new Primitive.TextureEntry(bake).GetBytes();
                client.Network.PacketEvents.InvokeRaiseEvent(PacketType.AvatarAppearance, packet, sim);
            }
            Assert.That(bakes, Is.EqualTo(new[] { first, second }));
        }
    }
}
