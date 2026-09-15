using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>
    /// Turns a built <see cref="WaxGlyphGeometry"/> into the mesh, texture and material
    /// WaxCandy V7 renders. Main thread only.
    /// </summary>
    public static class WaxGlyphAssets
    {
        public static readonly int EdgeDistanceMapId = Shader.PropertyToID("_EdgeDistanceMap");
        public static readonly int DistanceRangeId = Shader.PropertyToID("_DistanceRange");
        public static readonly int DistanceMapRectId = Shader.PropertyToID("_DistanceMapRect");
        public static readonly int LetterBoundsId = Shader.PropertyToID("_LetterBounds");
        public static readonly int FrontAxisId = Shader.PropertyToID("_FrontAxis");
        public static readonly int DepthCenterId = Shader.PropertyToID("_DepthCenter");
        public static readonly int HalfDepthId = Shader.PropertyToID("_HalfDepth");
        public static readonly int OpenBottomId = Shader.PropertyToID("_OpenBottom");
        public static readonly int SourceLetterBoundsId = Shader.PropertyToID("_SourceLetterBounds");
        public static readonly int ContactSpanAId = Shader.PropertyToID("_ContactSpanA");
        public static readonly int ContactSpanBId = Shader.PropertyToID("_ContactSpanB");
        public static readonly int ContactInfoId = Shader.PropertyToID("_ContactInfo");
        public static readonly int SpreadCurveId = Shader.PropertyToID("_SpreadCurve");
        public static readonly int FlowRadialId = Shader.PropertyToID("_FlowRadial");

        public const string PedestalSpreadKeyword = "_PEDESTAL_SPREAD";

        /// <summary>Contact spans the shader takes; any more are folded into the last one.</summary>
        public const int MaxContactSpans = 4;

        /// <summary>Uploads the mesh and releases its CPU copy.</summary>
        public static Mesh CreateMesh(WaxGlyphGeometry geometry)
        {
            var mesh = new Mesh { name = $"WaxGlyph {Describe(geometry.Codepoint)}" };
            if (geometry.Positions.Length > ushort.MaxValue)
            {
                mesh.indexFormat = IndexFormat.UInt32;
            }

            mesh.SetVertices(geometry.Positions);
            mesh.SetNormals(geometry.Normals);
            mesh.SetUVs(0, geometry.Uvs);
            mesh.SetTriangles(geometry.Triangles, 0, true);
            mesh.UploadMeshData(true);
            return mesh;
        }

        /// <summary>
        /// Uploads the distance map as linear R16, or R8 where R16 is not supported, with
        /// no mipmaps and clamped bilinear sampling like the reference letter's map.
        /// </summary>
        public static Texture2D CreateDistanceMap(WaxGlyphGeometry geometry)
        {
            bool sixteenBit = SystemInfo.SupportsTextureFormat(TextureFormat.R16);
            var texture = new Texture2D(
                geometry.MapWidth,
                geometry.MapHeight,
                sixteenBit ? TextureFormat.R16 : TextureFormat.R8,
                false,
                true)
            {
                name = $"WaxGlyph {Describe(geometry.Codepoint)} Edge Distance",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            if (sixteenBit)
            {
                texture.SetPixelData(geometry.DistanceMap, 0);
            }
            else
            {
                var narrow = new byte[geometry.DistanceMap.Length];
                for (int i = 0; i < narrow.Length; i++)
                {
                    narrow[i] = (byte)((geometry.DistanceMap[i] + 128) / 257);
                }

                texture.SetPixelData(narrow, 0);
            }

            texture.Apply(false, true);
            return texture;
        }

        /// <summary>Instances the template and fills in the values that belong to this glyph.</summary>
        public static Material CreateMaterial(Material template, WaxGlyphGeometry geometry, Texture2D distanceMap)
        {
            var material = new Material(template) { name = $"{template.name} {Describe(geometry.Codepoint)}" };
            material.SetTexture(EdgeDistanceMapId, distanceMap);
            material.SetFloat(DistanceRangeId, geometry.DistanceRange);
            material.SetVector(DistanceMapRectId, geometry.MapRect);
            material.SetVector(LetterBoundsId, geometry.LetterBounds);
            material.SetVector(FrontAxisId, new Vector4(0f, 0f, 1f, 0f));
            material.SetFloat(DepthCenterId, 0f);
            material.SetFloat(HalfDepthId, geometry.HalfDepth);
            return material;
        }

        /// <summary>
        /// Instances the template for a pedestal laid flat under <paramref name="glyph"/>, wearing
        /// the glyph's own layers spread outward from where it stands.
        /// </summary>
        /// <param name="contactSpans">Where the glyph stands, (x start, x end), sorted, as <see cref="WaxGlyphPedestal.Build"/> gives them.</param>
        public static Material CreatePedestalMaterial(
            Material template,
            WaxGlyphGeometry glyph,
            WaxGlyphGeometry pedestal,
            Texture2D distanceMap,
            IReadOnlyList<Vector2> contactSpans,
            float colorReach,
            float colorCurve)
        {
            Material material = CreateMaterial(template, pedestal, distanceMap);
            material.name = $"{template.name} Pedestal {Describe(pedestal.Codepoint)}";
            material.SetVector(FrontAxisId, new Vector4(0f, 1f, 0f, 0f));
            material.SetFloat(DepthCenterId, pedestal.HalfDepth);
            material.SetFloat(OpenBottomId, 0f);

            // The melt flow runs down letters; this marks a pedestal so it is left out.
            material.SetFloat(FlowRadialId, 1f);

            int count = Mathf.Min(contactSpans.Count, MaxContactSpans);
            var packed = new float[2 * MaxContactSpans];
            for (int i = 0; i < count; i++)
            {
                packed[2 * i] = contactSpans[i].x;
                packed[2 * i + 1] = contactSpans[i].y;
            }

            // Spans are sorted, so stretching the last slot to the last end covers the rest.
            if (contactSpans.Count > MaxContactSpans)
            {
                packed[2 * MaxContactSpans - 1] = contactSpans[contactSpans.Count - 1].y;
            }

            material.SetVector(ContactSpanAId, new Vector4(packed[0], packed[1], packed[2], packed[3]));
            material.SetVector(ContactSpanBId, new Vector4(packed[4], packed[5], packed[6], packed[7]));
            material.SetVector(ContactInfoId, new Vector4(count, glyph.HalfDepth, colorReach, 0f));
            material.SetVector(SourceLetterBoundsId, glyph.LetterBounds);
            material.SetFloat(SpreadCurveId, colorCurve);
            material.EnableKeyword(PedestalSpreadKeyword);
            return material;
        }

        public static string Describe(int codepoint) =>
            codepoint >= 0x20 && codepoint <= 0x10FFFF && (codepoint < 0xD800 || codepoint > 0xDFFF)
                ? $"'{char.ConvertFromUtf32(codepoint)}'"
                : $"U+{codepoint:X4}";
    }
}
