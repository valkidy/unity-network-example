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

        public static string Describe(int codepoint) =>
            codepoint >= 0x20 && codepoint <= 0x10FFFF && (codepoint < 0xD800 || codepoint > 0xDFFF)
                ? $"'{char.ConvertFromUtf32(codepoint)}'"
                : $"U+{codepoint:X4}";
    }
}
