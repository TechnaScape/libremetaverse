using System;
using LibreMetaverse.StructuredData;

namespace LibreMetaverse.Materials
{
    public class LegacyMaterial
    {
        public UUID ID { get; set; }
        
        public UUID NormalMap { get; set; }
        public double NormalMapOffsetX { get; set; }
        public double NormalMapOffsetY { get; set; }
        public double NormalMapRepeatX { get; set; }
        public double NormalMapRepeatY { get; set; }
        public double NormalMapRotation { get; set; }

        public UUID SpecularMap { get; set; }
        public double SpecularMapOffsetX { get; set; }
        public double SpecularMapOffsetY { get; set; }
        public double SpecularMapRepeatX { get; set; }
        public double SpecularMapRepeatY { get; set; }
        public double SpecularMapRotation { get; set; }

        public Color4 SpecularColor { get; set; }
        public byte SpecularExponent { get; set; }
        public byte EnvironmentIntensity { get; set; }
        public byte AlphaMaskCutoff { get; set; }

        public LegacyMaterialAlphaMode DiffuseAlphaMode { get; set; }

        public LegacyMaterial()
        {

        }

        private const double MaterialsMultiplier = 10000.0;

        /// <summary>
        /// Reads a materials-capability colour, whose components are the raw bytes of an
        /// <c>LLColor4U</c> rather than floats.
        /// </summary>
        /// <remarks>
        /// <para><c>LLColor4U::getValue()</c> assigns each <c>U8</c> component straight into an
        /// LLSD array, so <c>SpecColor</c> arrives as four integers in 0..255 and the viewer
        /// divides by 255 on the way back in (<c>llvovolume.cpp</c>, building
        /// <c>LLDrawInfo::mSpecColor</c>).</para>
        ///
        /// <para><see cref="OSD.AsColor4"/> cannot know that: it reads each element as a real and
        /// clamps to [0, 1], so every component from 1 to 255 saturates to 1.0 and only a literal
        /// zero survives. Used here that turned every specular tint in Second Life into pure white
        /// at full intensity -- which is not a small error on skin, where creators dial the
        /// specular colour down precisely to stop a body reading as wet.</para>
        ///
        /// <para>Anything that is not a four element array falls back to the viewer's own default,
        /// <c>LLMaterial::DEFAULT_SPECULAR_LIGHT_COLOR</c>, which is opaque white.</para>
        /// </remarks>
        private static Color4 ColorFromBytes(OSD osd)
        {
            if (!(osd is OSDArray array) || array.Count != 4)
            {
                return new Color4(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue);
            }

            return new Color4(
                ClampToByte(array[0]), ClampToByte(array[1]),
                ClampToByte(array[2]), ClampToByte(array[3]));
        }

        private static byte ClampToByte(OSD component)
        {
            int value = component.AsInteger();

            if (value < 0) { return 0; }
            if (value > byte.MaxValue) { return byte.MaxValue; }

            return (byte)value;
        }


        public LegacyMaterial(OSDMap mapOrig)
        {
            if (!(mapOrig.ContainsKey("ID") && mapOrig.ContainsKey("Material")))
            {
                throw new InvalidOperationException("Legacy material needs to contain 'ID' and 'Material' keys.");
            }

            if (mapOrig["ID"] is OSDBinary idBinary)
            {
                ID = new UUID(idBinary.AsBinary(), 0);
            } 
            else if (mapOrig["ID"] is OSDArray idArray)
            {
                ID = new UUID(idArray.AsBinary(), 0);
            } 
            else if (mapOrig["ID"] is OSDUUID idUUID)
            {
                ID = idUUID.AsUUID();
            }
            else
            {
                throw new InvalidOperationException("LegacyMaterial ID is of an unknown type " + mapOrig["ID"].Type);
            }
            
            if (mapOrig["Material"] is OSDMap map)
            {
                NormalMap = map["NormMap"].AsUUID();
                NormalMapOffsetX = map["NormOffsetX"].AsInteger() / MaterialsMultiplier;
                NormalMapOffsetY = map["NormOffsetY"].AsInteger() / MaterialsMultiplier;
                NormalMapRepeatX = map["NormRepeatX"].AsInteger() / MaterialsMultiplier;
                NormalMapRepeatY = map["NormRepeatY"].AsInteger() / MaterialsMultiplier;
                NormalMapRotation = map["NormRotation"].AsInteger() / MaterialsMultiplier;

                SpecularMap = map["SpecMap"].AsUUID();
                SpecularMapOffsetX = map["SpecOffsetX"].AsInteger() / MaterialsMultiplier;
                SpecularMapOffsetY = map["SpecOffsetY"].AsInteger() / MaterialsMultiplier;
                SpecularMapRepeatX = map["SpecRepeatX"].AsInteger() / MaterialsMultiplier;
                SpecularMapRepeatY = map["SpecRepeatY"].AsInteger() / MaterialsMultiplier;
                SpecularMapRotation = map["SpecRotation"].AsInteger() / MaterialsMultiplier;

                SpecularColor = ColorFromBytes(map["SpecColor"]);
                SpecularExponent = (byte)map["SpecExp"].AsInteger();
                EnvironmentIntensity = (byte)map["EnvIntensity"].AsInteger();
                AlphaMaskCutoff = (byte)map["AlphaMaskCutoff"].AsInteger();
                DiffuseAlphaMode = (LegacyMaterialAlphaMode)map["DiffuseAlphaMode"].AsInteger();
            }
        }
    }
}
