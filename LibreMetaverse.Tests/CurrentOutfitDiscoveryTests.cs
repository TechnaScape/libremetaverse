/*
 * Copyright (c) 2026, Sjofn LLC.
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without modification, are
 * permitted under the terms of the project's BSD-3-Clause licence.
 */

using System.Collections.Generic;
using NUnit.Framework;

namespace LibreMetaverse.Tests
{
    [TestFixture]
    [Category("Inventory")]
    public sealed class CurrentOutfitDiscoveryTests
    {
        [Test]
        public void LoginSkeletonFindsTheTypedCurrentOutfitFolderWithoutHttp()
        {
            var decoy = new InventoryFolder(UUID.Random())
            {
                Name = "Current Outfit",
                PreferredType = FolderType.None,
            };

            var expected = new InventoryFolder(UUID.Random())
            {
                Name = "Ropa actual",
                PreferredType = FolderType.CurrentOutfit,
            };

            InventoryFolder actual = AppearanceManager.FindCurrentOutfitFolder(
                new List<InventoryBase> { decoy, expected });

            Assert.That(actual, Is.SameAs(expected));
        }

        [Test]
        public void MissingSkeletonEntryRequestsTheCompatibilityFallback()
        {
            InventoryFolder actual = AppearanceManager.FindCurrentOutfitFolder(
                new List<InventoryBase>());

            Assert.That(actual, Is.Null);
        }
    }
}
