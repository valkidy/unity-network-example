using UnityEngine;

namespace NetworkExample.UnityDemo.Shatter
{
    /// <summary>
    /// One connected piece of a source mesh, standing on a pivot of its own.
    /// </summary>
    /// <remarks>
    /// The vertices are the source's, moved so the piece's bounds are centred on
    /// the origin, and <see cref="Pivot"/> is where that centre sat in the source
    /// mesh. Put a chunk at its pivot and it is back where it was drawn before
    /// the mesh was split, which is what lets a shattered prefab stand exactly
    /// where the intact one stood.
    ///
    /// The centre of the bounds rather than the average of the vertices, because
    /// the average follows tessellation: a beam with a detailed cap at one end
    /// would spin about that cap instead of about the middle of the beam.
    ///
    /// Normals and UVs are carried over untouched -- a translation changes
    /// neither -- so a chunk still samples the same atlas as the whole model.
    /// A piece cut by Blast also carries the faces the cut made, which sample
    /// nothing in the atlas; those are kept apart in
    /// <see cref="InteriorTriangles"/> so they can be drawn with a material of
    /// their own.
    /// </remarks>
    public sealed class MeshIsland
    {
        internal MeshIsland(
            Vector3[] positions,
            Vector3[] normals,
            Vector2[] uvs,
            Vector4[] tangents,
            int[] triangles,
            Vector3 pivot,
            Bounds localBounds)
            : this(positions, normals, uvs, tangents, triangles, System.Array.Empty<int>(), pivot, localBounds)
        {
        }

        internal MeshIsland(
            Vector3[] positions,
            Vector3[] normals,
            Vector2[] uvs,
            Vector4[] tangents,
            int[] triangles,
            int[] interiorTriangles,
            Vector3 pivot,
            Bounds localBounds)
        {
            Positions = positions;
            Normals = normals;
            Uvs = uvs;
            Tangents = tangents;
            Triangles = triangles;
            InteriorTriangles = interiorTriangles ?? System.Array.Empty<int>();
            Pivot = pivot;
            LocalBounds = localBounds;
        }

        /// <summary>Vertices relative to <see cref="Pivot"/>.</summary>
        public Vector3[] Positions { get; }

        /// <summary>Empty when the source mesh carried no normals.</summary>
        public Vector3[] Normals { get; }

        /// <summary>Empty when the source mesh carried no UVs.</summary>
        public Vector2[] Uvs { get; }

        /// <summary>Empty when the source mesh carried no tangents.</summary>
        public Vector4[] Tangents { get; }

        /// <summary>The model's own surface, drawn with the model's material.</summary>
        public int[] Triangles { get; }

        /// <summary>
        /// Faces a cut made that carry no part of the model's surface, drawn with
        /// a material of their own. Empty for a piece that has none, which is
        /// every piece this project cuts itself: its caps wear the surface's UVs
        /// and go in <see cref="Triangles"/>.
        /// </summary>
        public int[] InteriorTriangles { get; }

        /// <summary>Where the piece's bounds centre sat in the source mesh.</summary>
        public Vector3 Pivot { get; }

        /// <summary>Bounds around the origin, so already centred on the pivot.</summary>
        public Bounds LocalBounds { get; }

        public int VertexCount => Positions.Length;

        /// <summary>Surface and interior triangles together.</summary>
        public int TriangleCount => (Triangles.Length + InteriorTriangles.Length) / 3;

        /// <summary>
        /// Whether no edge of the piece is used by only one triangle, with
        /// vertices welded by exact position.
        /// </summary>
        /// <remarks>
        /// Welded because an exporter splits a vertex along every UV seam and
        /// hard edge, which would otherwise read as a rim everywhere. A piece
        /// that passes has no hole anything can be seen through -- which is what
        /// a cutter that builds its faces by boolean operations needs of its
        /// input before it can tell inside from outside.
        /// </remarks>
        public bool IsClosed()
        {
            var weld = new System.Collections.Generic.Dictionary<Vector3, int>(Positions.Length);
            var ids = new int[Positions.Length];
            for (int index = 0; index < Positions.Length; ++index)
            {
                Vector3 p = Positions[index];
                var key = new Vector3(p.x == 0f ? 0f : p.x, p.y == 0f ? 0f : p.y, p.z == 0f ? 0f : p.z);
                if (!weld.TryGetValue(key, out int id))
                {
                    id = weld.Count;
                    weld.Add(key, id);
                }

                ids[index] = id;
            }

            var uses = new System.Collections.Generic.Dictionary<long, int>();
            CountEdges(Triangles, ids, uses);
            CountEdges(InteriorTriangles, ids, uses);
            foreach (int used in uses.Values)
            {
                if (used == 1)
                {
                    return false;
                }
            }

            return uses.Count > 0;
        }

        private static void CountEdges(
            int[] triangles,
            int[] ids,
            System.Collections.Generic.Dictionary<long, int> uses)
        {
            for (int start = 0; start + 2 < triangles.Length; start += 3)
            {
                for (int corner = 0; corner < 3; ++corner)
                {
                    int from = ids[triangles[start + corner]];
                    int to = ids[triangles[start + ((corner + 1) % 3)]];
                    if (from == to)
                    {
                        continue;
                    }

                    long key = from < to
                        ? ((long)from << 32) | (uint)to
                        : ((long)to << 32) | (uint)from;
                    uses[key] = uses.TryGetValue(key, out int used) ? used + 1 : 1;
                }
            }
        }

        /// <summary>
        /// Builds the piece as a mesh ready to be drawn or written to an asset.
        /// </summary>
        /// <remarks>
        /// The index format is picked from the vertex count rather than left at
        /// the default, because a piece large enough to need 32-bit indices would
        /// otherwise fail to take its own triangles.
        /// </remarks>
        public Mesh CreateMesh(string name)
        {
            var mesh = new Mesh
            {
                name = name,
                indexFormat = Positions.Length > 65535
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16,
            };

            mesh.vertices = Positions;
            if (Normals.Length == Positions.Length)
            {
                mesh.normals = Normals;
            }

            if (Uvs.Length == Positions.Length)
            {
                mesh.uv = Uvs;
            }

            if (Tangents.Length == Positions.Length)
            {
                mesh.tangents = Tangents;
            }

            if (InteriorTriangles.Length == 0)
            {
                mesh.triangles = Triangles;
            }
            else
            {
                // The surface first, so a renderer that only has the model's
                // material still draws the part of the piece that is the model.
                mesh.subMeshCount = 2;
                mesh.SetTriangles(Triangles, 0);
                mesh.SetTriangles(InteriorTriangles, 1);
            }

            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
