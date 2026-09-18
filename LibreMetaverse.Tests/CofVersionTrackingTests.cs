using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using LibreMetaverse.StructuredData;
using LibreMetaverse.Tests.TestHelpers;

namespace LibreMetaverse.Tests
{
    /// <summary>
    /// [SLUnity] The store's copy of a folder's version must follow every AIS write, because the
    /// Current Outfit folder's version is what a server-side bake request has to match. A write that
    /// dropped its reply's <c>_updated_category_versions</c> left the store behind, every bake after
    /// an outfit change was refused ("Server expected COF version N+1, we sent N") and the server kept
    /// the previous bake.
    /// </summary>
    [TestFixture]
    [Category("Inventory")]
    public class CofVersionTrackingTests
    {
        private static readonly Uri FakeCap = new Uri("http://fake-ais3.test/ais3");

        private static FakeGridClient MakeClient(UUID folderId, int version)
        {
            var client = new FakeGridClient();
            client.SetInventoryAndLibraryCaps(FakeCap, new Uri("http://fake-lib.test/lib/"));
            client.SeedInventoryFolder(folderId, "Current Outfit");
            StoredFolder(client, folderId).Version = version;
            return client;
        }

        private static InventoryFolder StoredFolder(GridClient client, UUID folderId)
            => (InventoryFolder)client.Inventory.Store![folderId];

        private static OSDMap LinkOsd(UUID folderId, UUID linkedId, string name, AssetType type, InventoryType invType)
            => new OSDMap
            {
                { "item_id", OSD.FromUUID(UUID.Random()) },
                { "parent_id", OSD.FromUUID(folderId) },
                { "linked_id", OSD.FromUUID(linkedId) },
                { "name", name },
                { "desc", string.Empty },
                { "agent_id", OSD.FromUUID(UUID.Random()) },
                { "inv_type", (int)invType },
                { "type", (int)type },
                { "created_at", (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
            };

        private static string Reply(UUID folderId, int? version, params OSDMap[] links)
        {
            var linksMap = new OSDMap();
            for (var i = 0; i < links.Length; i++) linksMap[i.ToString()] = links[i];
            var reply = new OSDMap { { "_embedded", new OSDMap { { "links", linksMap } } } };
            if (version.HasValue)
                reply["_updated_category_versions"] = new OSDMap { { folderId.ToString(), OSD.FromInteger(version.Value) } };
            return OSDParser.SerializeLLSDXmlString(reply);
        }

        [Test]
        public async Task ABatchLinkCreationRecordsTheFolderVersionItsReplyNames()
        {
            var cof = UUID.Random();
            using var client = MakeClient(cof, 19961);
            var outfit = new InventoryFolder(UUID.Random()) { Name = "Just a chill Goat" };
            client.AddHttpResponseForPath($"http://fake-ais3.test/ais3/category/{cof}", HttpStatusCode.OK,
                Reply(cof, 19962, LinkOsd(cof, outfit.UUID, outfit.Name, AssetType.LinkFolder, InventoryType.Folder)));

            bool? accepted = null;
            await client.Inventory.CreateLinksAsync(cof, new[] { ((InventoryBase)outfit, string.Empty) },
                ok => accepted = ok, CancellationToken.None);

            Assert.That(accepted, Is.True);
            Assert.That(StoredFolder(client, cof).Version, Is.EqualTo(19962),
                "the reply's version must reach the store, or the next bake request is one behind");
        }

        [Test]
        public async Task AFolderLinkIsParsedAsAFolderLink()
        {
            var cof = UUID.Random();
            using var client = MakeClient(cof, 1);
            var folderTarget = UUID.Random();
            var itemTarget = UUID.Random();
            client.AddHttpResponseForPath($"http://fake-ais3.test/ais3/category/{cof}", HttpStatusCode.OK,
                Reply(cof, 2,
                    LinkOsd(cof, folderTarget, "Outfit", AssetType.LinkFolder, InventoryType.Folder),
                    LinkOsd(cof, itemTarget, "Shirt", AssetType.Link, InventoryType.Wearable)));

            var links = await client.AisClient.CreateInventoryLinksAsync(cof, new OSDMap(), CancellationToken.None);

            Assert.That(links.Single(l => l.AssetUUID == folderTarget).AssetType, Is.EqualTo(AssetType.LinkFolder));
            Assert.That(links.Single(l => l.AssetUUID == itemTarget).AssetType, Is.EqualTo(AssetType.Link));
        }

        [Test]
        public async Task ALinkSlamRecordsTheFolderVersionItsReplyNames()
        {
            var outfitFolder = UUID.Random();
            using var client = MakeClient(outfitFolder, 7);
            client.AddHttpResponseForPath($"http://fake-ais3.test/ais3/category/{outfitFolder}/links", HttpStatusCode.OK,
                Reply(outfitFolder, 8));

            Assert.That(await client.AisClient.PutCategoryLinksAsync(outfitFolder.ToString(), new OSDArray(), CancellationToken.None), Is.True);
            Assert.That(StoredFolder(client, outfitFolder).Version, Is.EqualTo(8));
        }

        [Test]
        public async Task AMutationWhoseReplyHasNoBodyStillSucceeds()
        {
            var outfitFolder = UUID.Random();
            using var client = MakeClient(outfitFolder, 7);
            client.AddHttpResponseForPath($"http://fake-ais3.test/ais3/category/{outfitFolder}/links", HttpStatusCode.OK,
                string.Empty);

            Assert.That(await client.AisClient.SlamFolderAsync(outfitFolder, new OSDArray(), CancellationToken.None), Is.True);
            Assert.That(StoredFolder(client, outfitFolder).Version, Is.EqualTo(7));
        }

        [Test]
        public async Task ALateReplyNeverLowersAFolderVersion()
        {
            var cof = UUID.Random();
            using var client = MakeClient(cof, 50);
            client.AddHttpResponseForPath($"http://fake-ais3.test/ais3/category/{cof}", HttpStatusCode.OK,
                Reply(cof, 42, LinkOsd(cof, UUID.Random(), "Shirt", AssetType.Link, InventoryType.Wearable)));

            await client.AisClient.CreateInventoryLinksAsync(cof, new OSDMap(), CancellationToken.None);

            Assert.That(StoredFolder(client, cof).Version, Is.EqualTo(50));
        }

        [Test]
        public void AStatedVersionRaisesAFolderAndNeverLowersIt()
        {
            var cof = UUID.Random();
            using var client = MakeClient(cof, 19961);

            Assert.That(client.Inventory.RaiseFolderVersion(cof, 19962), Is.True);
            Assert.That(StoredFolder(client, cof).Version, Is.EqualTo(19962));

            Assert.That(client.Inventory.RaiseFolderVersion(cof, 19900), Is.False);
            Assert.That(StoredFolder(client, cof).Version, Is.EqualTo(19962));

            Assert.That(client.Inventory.RaiseFolderVersion(UUID.Random(), 5), Is.False, "an unknown folder is left alone");
        }
    }
}
