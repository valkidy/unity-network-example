using System.Collections.Generic;
using UnityEngine;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>
    /// Everything built for one character off the main thread: mesh arrays, the edge
    /// distance map, the material values that belong with them, and build diagnostics.
    /// </summary>
    public sealed class WaxGlyphGeometry
    {
        internal WaxGlyphGeometry(int codepoint)
        {
            Codepoint = codepoint;
        }

        public int Codepoint { get; }

        /// <summary>0 when the font has no glyph for the character.</summary>
        public int GlyphIndex { get; internal set; }

        /// <summary>Pen advance in glyph object units.</summary>
        public float Advance { get; internal set; }

        /// <summary>True for characters with no outline, such as a space.</summary>
        public bool IsEmpty => Triangles == null;

        public Vector3[] Positions { get; internal set; }

        public Vector3[] Normals { get; internal set; }

        /// <summary>Planar projection of every vertex into <see cref="MapRect"/>, walls included.</summary>
        public Vector2[] Uvs { get; internal set; }

        public int[] Triangles { get; internal set; }

        public int TriangleCount => Triangles == null ? 0 : Triangles.Length / 3;

        /// <summary>
        /// Triangles in each cap. The index buffer holds the front cap (+Z), then the back
        /// cap, then the walls.
        /// </summary>
        public int CapTriangleCount { get; internal set; }

        /// <summary>Wall bands between the front and back rims.</summary>
        public int WallSegments { get; internal set; }

        /// <summary>Vertices in one ring; the mesh has <see cref="WallSegments"/> + 1 rings.</summary>
        public int OutlineVertexCount { get; internal set; }

        public int RegionCount { get; internal set; }

        public int HoleCount { get; internal set; }

        /// <summary>False when triangulation met a self-crossing outline and forced a triangle.</summary>
        public bool TriangulationClean { get; internal set; }

        /// <summary>True when the outline was simplified to fit the triangle budget.</summary>
        public bool Simplified { get; internal set; }

        /// <summary>True when even the simplified outline could not fit the budget.</summary>
        public bool OverBudget { get; internal set; }

        /// <summary>Object-space XY bounds (xMin, yMin, xMax, yMax) for the material's _LetterBounds.</summary>
        public Vector4 LetterBounds { get; internal set; }

        /// <summary>Object-space area the map covers (xMin, yMin, width, height) for _DistanceMapRect.</summary>
        public Vector4 MapRect { get; internal set; }

        public int MapWidth { get; internal set; }

        public int MapHeight { get; internal set; }

        /// <summary>Distance / <see cref="DistanceRange"/> as 16-bit values, rows bottom to top.</summary>
        public ushort[] DistanceMap { get; internal set; }

        public float DistanceRange { get; internal set; }

        public float HalfDepth { get; internal set; }

        /// <summary>
        /// Deepest point inside the outline, which is half the thickest stroke. WaxCandy's
        /// body layers only show where this exceeds the material's outline width.
        /// </summary>
        public float MaxInteriorDistance { get; internal set; }

        /// <summary>Union of the font contours after corner rounding; the map is baked from these.</summary>
        public IReadOnlyList<List<GlyphVector>> Silhouette { get; internal set; }

        /// <summary>Regions the mesh was built from, after any budget simplification.</summary>
        public IReadOnlyList<GlyphRegion> MeshRegions { get; internal set; }

        public double OutlineMilliseconds { get; internal set; }

        public double UnionMilliseconds { get; internal set; }

        public double SimplifyMilliseconds { get; internal set; }

        public double MeshMilliseconds { get; internal set; }

        public double MapMilliseconds { get; internal set; }

        public double TotalMilliseconds =>
            OutlineMilliseconds + UnionMilliseconds + SimplifyMilliseconds + MeshMilliseconds + MapMilliseconds;
    }
}
