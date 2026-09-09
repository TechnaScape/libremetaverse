/*
 * Copyright (c) 2026, Sjofn LLC.
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without modification, are
 * permitted under the terms of the project's BSD-3-Clause licence.
 */

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using LibreMetaverse.StructuredData;
using LibreMetaverse.Tests.TestHelpers;
using NUnit.Framework;

namespace LibreMetaverse.Tests
{
    [TestFixture]
    [Category("Inventory")]
    public sealed class InventoryAisReadPreferenceTests
    {
        [Test]
        public async Task FolderContentsPrefersAisAndCachesReturnedItems()
        {
            using var client = new FakeGridClient();
            var cap = new Uri("http://fake-ais3.test/ais3");
            var folder = UUID.Random();
            var item = UUID.Random();

            client.SetInventoryAndLibraryCaps(cap, null!);
            client.SeedInventoryFolder(folder, "Objects");

            var permissions = new OSDMap
            {
                ["creator_id"] = UUID.Random(),
                ["last_owner_id"] = client.Self.AgentID,
                ["owner_id"] = client.Self.AgentID,
                ["base_mask"] = 0,
                ["everyone_mask"] = 0,
                ["group_mask"] = 0,
                ["next_owner_mask"] = 0,
                ["owner_mask"] = 0,
                ["is_owner_group"] = false,
                ["group_id"] = UUID.Zero,
            };
            var itemData = new OSDMap
            {
                ["item_id"] = item,
                ["parent_id"] = folder,
                ["name"] = "AIS object",
                ["desc"] = string.Empty,
                ["agent_id"] = client.Self.AgentID,
                ["inv_type"] = (int)InventoryType.Object,
                ["type"] = (int)AssetType.Object,
                ["created_at"] = 1,
                ["permissions"] = permissions,
                ["sale_info"] = new OSDMap
                {
                    ["sale_price"] = 0,
                    ["sale_type"] = (int)SaleType.Not,
                },
            };
            var response = new OSDMap
            {
                ["_embedded"] = new OSDMap
                {
                    ["items"] = new OSDMap { [item.ToString()] = itemData },
                },
            };

            client.AddHttpResponse(
                new Uri($"{cap}/category/{folder}/children?depth=0"),
                HttpStatusCode.OK,
                OSDParser.SerializeLLSDXmlString(response),
                HttpCapsClient.LLSD_XML);

            var contents = await client.Inventory.RequestFolderContentsAsync(
                folder, client.Self.AgentID, fetchFolders: true, fetchItems: true,
                order: InventorySortOrder.ByName, cancellationToken: CancellationToken.None);

            Assert.That(contents, Has.Count.EqualTo(1));
            Assert.That(contents[0].UUID, Is.EqualTo(item));
            Assert.That(client.Inventory.Store!.TryGetValue(item, out var cached), Is.True);
            Assert.That(cached, Is.TypeOf<InventoryObject>());
            Assert.That(client.CapturedRequests, Has.Count.EqualTo(1));
            Assert.That(client.CapturedRequests[0].Method, Is.EqualTo(System.Net.Http.HttpMethod.Get));
        }

        [Test]
        public async Task MissingAisItemReturnsInsteadOfWaitingForALegacyEvent()
        {
            using var client = new FakeGridClient();
            var cap = new Uri("http://fake-ais3.test/ais3");
            var missing = UUID.Random();

            client.SetInventoryAndLibraryCaps(cap, null!);
            client.AddHttpResponse(new Uri($"{cap}/item/{missing}"), HttpStatusCode.NotFound,
                "Not Found", "text/plain");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            InventoryItem? item = await client.Inventory.FetchItemAsync(
                missing, client.Self.AgentID, timeout.Token);

            Assert.That(item, Is.Null);
            Assert.That(timeout.IsCancellationRequested, Is.False,
                "a definitive AIS 404 must not fall through to a legacy event that cannot arrive");
            Assert.That(client.CapturedRequests, Has.Count.EqualTo(1));
        }
    }
}
