using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkExample.UnityDemo.Shatter
{
    /// <summary>
    /// A mesh being cut up: the buffers a clip reads and writes, in the space of
    /// the piece it came from.
    /// </summary>
    /// <remarks>
    /// <see cref="MeshIsland"/> is what a bake produces and is finished; this is
    /// what the cutting works on. Normals and UVs are always carried, filled with
    /// zeroes for a source that had none, so the clip never has to ask whether a
    /// vertex has them -- and <see cref="ToIsland"/> puts an attribute the source
    /// never had back to empty rather than handing on a buffer of zeroes.
    ///
    /// Tangents are dropped. The one model this exists for has none, and a cap
    /// cut through a surface has no meaningful tangent to give; a caller with a
    /// normal-mapped model has to generate them after the bake rather than expect
    /// them to survive it.
    /// </remarks>
    public sealed class ShatterGeometry
    {
        public ShatterGeometry(bool carriesNormals, bool carriesUvs)
        {
            CarriesNormals = carriesNormals;
            CarriesUvs = carriesUvs;
        }

        public List<Vector3> Positions { get; } = new List<Vector3>();

        public List<Vector3> Normals { get; } = new List<Vector3>();

        public List<Vector2> Uvs { get; } = new List<Vector2>();

        public List<int> Triangles { get; } = new List<int>();

        /// <summary>False when the source mesh had no normals to carry.</summary>
        public bool CarriesNormals { get; }

        /// <summary>False when the source mesh had no UVs to carry.</summary>
        public bool CarriesUvs { get; }

        public int VertexCount => Positions.Count;

        public int TriangleCount => Triangles.Count / 3;

        public static ShatterGeometry From(MeshIsland island)
        {
            if (island == null)
            {
                throw new ArgumentNullException(nameof(island));
            }

            bool hasNormals = island.Normals.Length == island.Positions.Length;
            bool hasUvs = island.Uvs.Length == island.Positions.Length;
            var geometry = new ShatterGeometry(hasNormals, hasUvs);
            for (int index = 0; index < island.Positions.Length; ++index)
            {
                geometry.Positions.Add(island.Positions[index]);
                geometry.Normals.Add(hasNormals ? island.Normals[index] : Vector3.zero);
                geometry.Uvs.Add(hasUvs ? island.Uvs[index] : Vector2.zero);
            }

            geometry.Triangles.AddRange(island.Triangles);
            return geometry;
        }

        public void Add(Vector3 position, Vector3 normal, Vector2 uv)
        {
            Positions.Add(position);
            Normals.Add(normal);
            Uvs.Add(uv);
        }

        public void AddTriangle(int a, int b, int c)
        {
            Triangles.Add(a);
            Triangles.Add(b);
            Triangles.Add(c);
        }

        /// <summary>
        /// Finishes the piece: centred on its own bounds, with
        /// <paramref name="sourcePivot"/> carried through so the result still
        /// knows where it stood in the model.
        /// </summary>
        public MeshIsland ToIsland(Vector3 sourcePivot)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int index = 0; index < Positions.Count; ++index)
            {
                min = Vector3.Min(min, Positions[index]);
                max = Vector3.Max(max, Positions[index]);
            }

            Vector3 pivot = (min + max) * 0.5f;
            var positions = new Vector3[Positions.Count];
            for (int index = 0; index < Positions.Count; ++index)
            {
                positions[index] = Positions[index] - pivot;
            }

            return new MeshIsland(
                positions,
                CarriesNormals ? Normals.ToArray() : Array.Empty<Vector3>(),
                CarriesUvs ? Uvs.ToArray() : Array.Empty<Vector2>(),
                Array.Empty<Vector4>(),
                Triangles.ToArray(),
                sourcePivot + pivot,
                new Bounds(Vector3.zero, max - min));
        }
    }
}
