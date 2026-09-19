using System.Collections;
using LibreMetaverse.StructuredData;
using NUnit.Framework;

namespace LibreMetaverse.Tests
{
    /// <summary>
    /// The login reply's active gestures: asked for by the "gestures" login option and answered as
    /// { item_id, asset_id } maps. Left unparsed, every login began with no gesture active.
    /// </summary>
    [TestFixture]
    public class LoginGesturesTests
    {
        private static readonly UUID Item = new UUID("11111111-2222-3333-4444-555555555555");
        private static readonly UUID Asset = new UUID("66666666-7777-8888-9999-aaaaaaaaaaaa");

        [Test]
        public void ActiveGesturesAreReadFromAnLlsdReply()
        {
            var reply = new OSDMap
            {
                ["login"] = OSD.FromString("true"),
                ["gestures"] = new OSDArray
                {
                    new OSDMap { ["item_id"] = OSD.FromUUID(Item), ["asset_id"] = OSD.FromUUID(Asset) },
                    new OSDMap { ["item_id"] = OSD.FromUUID(UUID.Random()) },
                },
            };

            var data = new LoginResponseData();
            data.Parse(reply);

            Assert.That(data.Gestures.Count, Is.EqualTo(1), "an entry without an asset is not a gesture");
            Assert.That(data.Gestures[Item], Is.EqualTo(Asset));
        }

        [Test]
        public void ActiveGesturesAreReadFromAnXmlRpcReply()
        {
            var reply = new Hashtable
            {
                ["login"] = "true",
                ["gestures"] = new ArrayList
                {
                    new Hashtable { ["item_id"] = Item.ToString(), ["asset_id"] = Asset.ToString() },
                },
            };

            var data = new LoginResponseData();
            data.Parse(reply);

            Assert.That(data.Gestures.Count, Is.EqualTo(1));
            Assert.That(data.Gestures[Item], Is.EqualTo(Asset));
        }
    }
}
