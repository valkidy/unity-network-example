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

        public Mesh SourceMesh => sourceMesh;

        public Mesh[] Chunks => chunks;

        public Vector3[] Pivots => pivots;

        public int ChunkCount => chunks == null ? 0 : chunks.Length;

        public void Configure(Mesh source, Mesh[] chunkMeshes, Vector3[] chunkPivots)
        {
            sourceMesh = source;
            chunks = chunkMeshes;
            pivots = chunkPivots;
        }
    }
}
