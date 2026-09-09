/*
 * Copyright (c) 2026, Sjofn LLC.
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without modification, are
 * permitted under the terms of the project's BSD-3-Clause licence.
 */

using System.Collections.Generic;
using LibreMetaverse.StructuredData;
using NUnit.Framework;

namespace LibreMetaverse.Tests
{
    [TestFixture]
    [Category("Inventory")]
    public sealed class InventoryDescendentsResponseTests
    {
        [Test]
        public void PopulatedArraysAreParsedWhenDescendentCountIsStaleZero()
        {
            using var client = new GridClient();
            UUID owner = UUID.Random();
            UUID parent = UUID.Random();
            UUID child = UUID.Random();

            var category = new OSDMap
            {
                ["category_id"] = child,
                ["parent_id"] = parent,
                ["agent_id"] = owner,
                ["name"] = "Child",
                ["version"] = 3,
                ["type_default"] = (int)FolderType.None,
            };
            var folder = new OSDMap
            {
                ["folder_id"] = parent,
                ["owner_id"] = owner,
                ["version"] = 7,
                ["descendents"] = 0,
                ["categories"] = new OSDArray { category },
                ["items"] = new OSDArray(),
            };
            var response = new OSDMap
            {
                ["folders"] = new OSDArray { folder },
            };

            List<InventoryBase> parsed = client.Inventory.ParseFolderContentsResponse(
                response,
                new[] { new InventoryFolder(parent) { OwnerID = owner } });

            Assert.That(parsed, Has.Count.EqualTo(1));
            Assert.That(parsed[0], Is.TypeOf<InventoryFolder>());
            Assert.That(parsed[0].UUID, Is.EqualTo(child));
            Assert.That(parsed[0].ParentUUID, Is.EqualTo(parent));
        }
    }
}
