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
        /// Initializes a mesh asset that reads its header and sections from a seekable stream,
        /// starting at the stream's current position, instead of from <see cref="Asset.AssetData"/>.
        /// </summary>
        /// <remarks>
        /// A level of detail is one section of an asset that also carries three other levels, two
        /// physics shapes and a skin. Reading the whole asset into memory to inflate one of them is
        /// most of the allocation of a decode when the level asked for is a small one. Only the
        /// header and the sections asked for are read. The stream must stay open for as long as
        /// sections are decoded, and is not disposed by this asset.
        /// </remarks>
        public AssetMesh(UUID assetID, Stream source)
        {
            AssetID = assetID;
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _origin = source.Position;
        }

        private readonly Stream? _source;
        private readonly long _origin;

        /// <summary>Bytes read from the source stream or copied out of the asset data so far.</summary>
        public long BytesRead { get; private set; }

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
                Stream data = _source ?? new MemoryStream(AssetData, false);
                try
                {
                    if (_source != null) _source.Position = _origin;
                    OSDMap header = (OSDMap)OSDParser.DeserializeLLSDBinary(data);
                    _bodyStart = data.Position - (_source != null ? _origin : 0);
                    BytesRead += _bodyStart;

                    MeshData = new OSDMap();
                    MeshData["asset_header"] = header;
                    foreach (string partName in header.Keys)
                    {
                        MeshData[partName] = header[partName];
                    }

                    _decodedParts.Clear();
                    _header = header;
                }
                finally
                {
                    if (_source == null) data.Dispose();
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
                // Read into this thread's section buffer: the compressed bytes are needed only
                // for as long as the inflate takes, and a new array per section was a third of
                // what decoding a level left behind.
                byte[]? part = ReadSection(partInfo, reuse: true, out int length);
                if (part == null) return null;
                OSD decoded = Helpers.DecompressOSD(part, 0, length);
                MeshData[partName] = decoded;
                return decoded;
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to decode mesh asset section " + partName, ex);
                return null;
            }
        }

        /// <summary>
        /// The raw, still-compressed bytes of one named section, or null when the asset has no such
        /// section or it lies outside the asset.
        /// </summary>
        public byte[]? ReadPartBytes(string partName)
        {
            if (partName == null || !DecodeHeader()) return null;
            if (!_header!.TryGetValue(partName, out OSD entry) || entry.Type != OSDType.Map) return null;
            try
            {
                return ReadSection((OSDMap)entry, reuse: false, out _);
            }
            catch (Exception)
            {
                return null;
            }
        }

        [ThreadStatic] private static byte[]? _sectionBuffer;

        private byte[]? ReadSection(OSDMap partInfo, bool reuse, out int size)
        {
            size = 0;
            if (!partInfo.ContainsKey("offset") || !partInfo.ContainsKey("size")) return null;
            long offset = partInfo["offset"].AsInteger();
            size = partInfo["size"].AsInteger();
            if (offset < 0 || size <= 0) return null;

            long start = _bodyStart + offset;
            byte[] part;
            if (reuse)
            {
                part = _sectionBuffer ?? Array.Empty<byte>();
                if (part.Length < size)
                {
                    int grown = 1 << 16;
                    while (grown < size && grown < (1 << 30)) grown <<= 1;
                    part = new byte[grown];
                    _sectionBuffer = part;
                }
            }
            else
            {
                part = new byte[size];
            }

            if (_source == null)
            {
                if (start + size > AssetData.LongLength) return null;
                Buffer.BlockCopy(AssetData, (int)start, part, 0, size);
            }
            else
            {
                if (_origin + start + size > _source.Length) return null;
                _source.Position = _origin + start;
                int read = 0;
                while (read < size)
                {
                    int n = _source.Read(part, read, size - read);
                    if (n <= 0) return null;
                    read += n;
                }
            }

            BytesRead += size;
            return part;
        }
    }
}

