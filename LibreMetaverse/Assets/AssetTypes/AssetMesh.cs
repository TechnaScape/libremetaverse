/*
 * Copyright (c) 2006-2016, openmetaverse.co
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

using System;
using System.Collections.Generic;
using System.IO;
using LibreMetaverse.StructuredData;

namespace LibreMetaverse.Assets
{
    /// <summary>
    /// Represents Mesh asset
    /// </summary>
    public class AssetMesh : Asset
    {
        /// <summary>Override the base classes AssetType</summary>
        public override AssetType AssetType => AssetType.Mesh;

        /// <summary>
        /// Decoded mesh data
        /// </summary>
        public OSDMap MeshData = new OSDMap();

        /// <summary>Initializes a new instance of an AssetMesh object</summary>
        public AssetMesh() { }

        /// <summary>Initializes a new instance of an AssetMesh object with parameters</summary>
        /// <param name="assetID">A unique <see cref="UUID"/> specific to this asset</param>
        /// <param name="assetData">A byte array containing the raw asset data</param>
        public AssetMesh(UUID assetID, byte[] assetData)
            : base(assetID, assetData)
        {
        }

        /// <summary>
        /// TODO: Encodes Collada file into LLMesh format
        /// </summary>
        public sealed override void Encode() { }

        /// <summary>
        /// Decodes mesh asset. See <see cref="LibreMetaverse.Rendering.FacetedMesh.TryDecodeFromAsset"/>
        /// to furter decode it for rendering</summary>
        /// <returns>true</returns>
        public sealed override bool Decode()
        {
            if (!DecodeHeader()) return false;

            try
            {
                foreach (string partName in _header!.Keys)
                {
                    DecodePart(partName);
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to decode mesh asset", ex);
                return false;
            }
        }

        private OSDMap? _header;
        private long _bodyStart;
        private readonly HashSet<string> _decodedParts = new HashSet<string>();

        /// <summary>
        /// Reads the asset header only, leaving every compressed section where it is.
        /// </summary>
        /// <remarks>
        /// A mesh asset carries four levels of detail, two physics shapes and a skin, each a zlib
        /// stream of LLSD. <see cref="Decode"/> inflates and parses all of them, which is what a
        /// renderer asking for one level used to pay on every request -- several times the
        /// allocation and time of the level it wanted. Until a section is asked for through
        /// <see cref="DecodePart"/>, <see cref="MeshData"/> holds the header's own entry for it.
        /// </remarks>
        /// <returns>False when the header cannot be read.</returns>
        public bool DecodeHeader()
        {
            if (_header != null) return true;

            try
            {
                using (MemoryStream data = new MemoryStream(AssetData, false))
                {
                    OSDMap header = (OSDMap)OSDParser.DeserializeLLSDBinary(data);
                    _bodyStart = data.Position;

                    MeshData = new OSDMap();
                    MeshData["asset_header"] = header;
                    foreach (string partName in header.Keys)
                    {
                        MeshData[partName] = header[partName];
                    }

                    _decodedParts.Clear();
                    _header = header;
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to decode mesh asset header", ex);
                return false;
            }
        }

        /// <summary>
        /// Inflates one named section (<c>high_lod</c>, <c>skin</c>, ...) on first use and returns it.
        /// </summary>
        /// <returns>
        /// The decoded section; the header's value for an entry that is not a section; or null when
        /// the asset has no such entry, the section is empty, or it will not decode.
        /// </returns>
        public OSD? DecodePart(string partName)
        {
            if (partName == null || !DecodeHeader()) return null;
            if (!_header!.TryGetValue(partName, out OSD entry)) return null;
            if (entry.Type != OSDType.Map) return entry;
            if (_decodedParts.Contains(partName)) return MeshData[partName];

            OSDMap partInfo = (OSDMap)entry;
            _decodedParts.Add(partName);
            if (partInfo["offset"] < 0 || partInfo["size"] == 0)
            {
                return null;
            }

            try
            {
                byte[] part = new byte[partInfo["size"]];
                Buffer.BlockCopy(AssetData, partInfo["offset"] + (int)_bodyStart, part, 0, part.Length);
                OSD decoded = Helpers.DecompressOSD(part);
                MeshData[partName] = decoded;
                return decoded;
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to decode mesh asset section " + partName, ex);
                return null;
            }
        }
    }
}

