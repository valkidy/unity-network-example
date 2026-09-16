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
    /// neither -- so a chunk still samples the same atlas as the whole model and
    /// no piece needs a material of its own. What the split cannot give it is a
    /// surface where the mesh was never closed: an island is a piece the source
    /// already had, so it shows the source's own faces and nothing else.
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
        {
            Positions = positions;
            Normals = normals;
            Uvs = uvs;
            Tangents = tangents;
            Triangles = triangles;
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

        public int[] Triangles { get; }

        /// <summary>Where the piece's bounds centre sat in the source mesh.</summary>
        public Vector3 Pivot { get; }

        /// <summary>Bounds around the origin, so already centred on the pivot.</summary>
        public Bounds LocalBounds { get; }

        public int VertexCount => Positions.Length;

        public int TriangleCount => Triangles.Length / 3;

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

            mesh.triangles = Triangles;
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
