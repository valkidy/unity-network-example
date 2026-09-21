using UnityEngine;

namespace NetworkExample.UnityDemo.Shatter
{
    /// <summary>
    /// The pieces one model was split into, and where each of them stood.
    /// </summary>
    /// <remarks>
    /// The meshes live inside this asset rather than beside it, so a rebuild
    /// replaces them in place and every reference -- the shattered prefab's, a
    /// pool's -- keeps pointing at the same file.
    ///
    /// The shattered prefab already carries the same pieces as a hierarchy, and
    /// is what an effect instantiates. This is what the pieces belong to, and
    /// what a test can ask whether the bake still matches the model it was baked
    /// from.
    /// </remarks>
    public sealed class ShatterChunkSet : ScriptableObject
    {
        [SerializeField]
        [Tooltip("The mesh the chunks were split out of.")]
        private Mesh sourceMesh;

        [SerializeField]
        [Tooltip("Chunk meshes, largest first. Each is centred on its own pivot.")]
        private Mesh[] chunks;

        [SerializeField]
        [Tooltip("Where each chunk stood in the source mesh, in the same order.")]
        private Vector3[] pivots;

        [SerializeField]
        [Tooltip("The source mesh as it was when the chunks were baked. See Fingerprint.")]
        private string sourceFingerprint;

        public Mesh SourceMesh => sourceMesh;

        public Mesh[] Chunks => chunks;

        public Vector3[] Pivots => pivots;

        public int ChunkCount => chunks == null ? 0 : chunks.Length;

        public string SourceFingerprint => sourceFingerprint;

        public void Configure(Mesh source, Mesh[] chunkMeshes, Vector3[] chunkPivots)
        {
            sourceMesh = source;
            chunks = chunkMeshes;
            pivots = chunkPivots;
            sourceFingerprint = Fingerprint(source);
        }

        /// <summary>
        /// A short digest of a mesh's vertices and triangles.
        /// </summary>
        /// <remarks>
        /// What says the chunks still belong to the model they were cut from. The
        /// bake cannot simply be run again and compared -- part of it runs in
        /// Blast, which a machine checking the bake need not have -- so the model
        /// is recorded instead, and a changed model is caught by its digest no
        /// longer matching.
        /// </remarks>
        public static string Fingerprint(Mesh mesh)
        {
            if (mesh == null)
            {
                return string.Empty;
            }

            unchecked
            {
                ulong hash = 14695981039346656037UL;
                Vector3[] vertices = mesh.vertices;
                for (int index = 0; index < vertices.Length; ++index)
                {
                    hash = Mix(hash, System.BitConverter.SingleToInt32Bits(vertices[index].x));
                    hash = Mix(hash, System.BitConverter.SingleToInt32Bits(vertices[index].y));
                    hash = Mix(hash, System.BitConverter.SingleToInt32Bits(vertices[index].z));
                }

                int[] triangles = mesh.triangles;
                for (int index = 0; index < triangles.Length; ++index)
                {
                    hash = Mix(hash, triangles[index]);
                }

                return vertices.Length + "v-" + (triangles.Length / 3) + "t-" + hash.ToString("x16");
            }
        }

        private static ulong Mix(ulong hash, int value)
        {
            unchecked
            {
                for (int shift = 0; shift < 32; shift += 8)
                {
                    hash ^= (byte)(value >> shift);
                    hash *= 1099511628211UL;
                }

                return hash;
            }
        }
    }
}
