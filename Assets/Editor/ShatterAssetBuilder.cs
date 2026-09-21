using System.Collections.Generic;
using System.IO;
using NetworkExample.UnityDemo.Rendering;
using NetworkExample.UnityDemo.Shatter;
using UnityEditor;
using UnityEngine;

namespace NetworkExample.UnityDemo.EditorTools
{
    /// <summary>
    /// Bakes a model into the pieces it is already made of: a chunk set holding
    /// the meshes, and a prefab that stands those meshes exactly where the intact
    /// model drew them.
    /// </summary>
    /// <remarks>
    /// Baked rather than split when the effect plays, because the split is the
    /// expensive half and its answer never changes: the model is the same model
    /// every time a nest comes down. Doing it here also means the pieces can be
    /// looked at, counted and committed, instead of being whatever a machine
    /// happened to produce on the frame something exploded.
    ///
    /// The shattered prefab repeats the source prefab's transform chain down to
    /// the node that carried the mesh -- the tower's model node is turned a
    /// quarter turn and dropped below its pivot -- so instantiating it at an
    /// entity's pose puts every piece where that piece was already being drawn.
    /// A piece is a plain renderer with no collider and no body: what moves it is
    /// the effect's business, not the bake's.
    /// </remarks>
    public static class ShatterAssetBuilder
    {
        private const string TowerPrefabPath =
            "Assets/Resources/Props/tower/tower.prefab";
        private const string TowerChunkSetPath =
            "Assets/Resources/Props/tower/tower-shattered-chunks.asset";
        private const string TowerShatteredPrefabPath =
            "Assets/Resources/Props/tower/tower-shattered.prefab";
        private const string TowerInteriorMaterialPath =
            "Assets/Resources/Props/tower/tower-interior.mat";

        [MenuItem("Network Example/Presentation/Build Tower Shatter Assets")]
        public static void BuildTowerShatterAssets()
        {
            Build(
                TowerPrefabPath,
                TowerChunkSetPath,
                TowerShatteredPrefabPath,
                TowerInteriorMaterialPath);
        }

        /// <summary>
        /// Splits the one mesh under <paramref name="sourcePrefabPath"/> and
        /// writes both assets. Returns the shattered prefab, or null when the
        /// source is not something this can split.
        /// </summary>
        /// <param name="interiorMaterialPath">
        /// The material the faces Blast makes are drawn with, created there the
        /// first time it is needed and left alone after that, so it can be
        /// painted without a rebake painting over it.
        /// </param>
        public static GameObject Build(
            string sourcePrefabPath,
            string chunkSetPath,
            string shatteredPrefabPath,
            string interiorMaterialPath)
        {
            GameObject sourcePrefab =
                AssetDatabase.LoadAssetAtPath<GameObject>(sourcePrefabPath);
            if (sourcePrefab == null)
            {
                Debug.LogError("No prefab at " + sourcePrefabPath + ".");
                return null;
            }

            MeshFilter[] filters = sourcePrefab.GetComponentsInChildren<MeshFilter>(true);
            var drawn = new List<MeshFilter>();
            for (int index = 0; index < filters.Length; ++index)
            {
                if (filters[index].sharedMesh != null)
                {
                    drawn.Add(filters[index]);
                }
            }

            if (drawn.Count != 1)
            {
                Debug.LogError(
                    sourcePrefabPath + " draws " + drawn.Count + " meshes. This " +
                    "builds one, because a chunk keeps neither the renderer it " +
                    "came from nor which submesh it was in.");
                return null;
            }

            MeshFilter filter = drawn[0];
            Mesh sourceMesh = filter.sharedMesh;
            if (sourceMesh.subMeshCount != 1)
            {
                Debug.LogError(
                    sourceMesh.name + " has " + sourceMesh.subMeshCount +
                    " submeshes. A chunk is one submesh with one material, so a " +
                    "multi-material model would come back wearing the wrong one.");
                return null;
            }

            List<MeshIsland> parts = MeshIslandSplitter.Split(sourceMesh);
            if (parts.Count == 0)
            {
                Debug.LogError(sourceMesh.name + " has no triangles to split.");
                return null;
            }

            BlastCutter blast = BlastCutter.Find();
            if (blast == null)
            {
                Debug.LogWarning(
                    "blast_cut is not built, so closed parts are cut into voronoi cells " +
                    "like the rest. Build it with Tools/BlastSpike/README.md for noisy " +
                    "breaks; the bake this makes is not the one that is committed.");
            }

            List<MeshIsland> pieces = MeshIslandFracturer.Fracture(
                parts,
                MeshIslandFracturer.DefaultCutAboveExtent,
                MeshIslandFracturer.DefaultTargetPieceExtent,
                MeshIslandFracturer.DefaultSeed,
                blast != null ? blast.Cut : (MeshIslandFracturer.ClosedPartCutter)null,
                out int partsCut,
                out int partsCutByBlast,
                out int unclosedCuts);

            Material interior = NeedsInterior(pieces)
                ? EnsureInteriorMaterial(interiorMaterialPath, filter.GetComponent<MeshRenderer>())
                : null;
            ShatterChunkSet chunkSet = WriteChunkSet(chunkSetPath, sourceMesh, pieces);
            GameObject shattered = WritePrefab(
                shatteredPrefabPath,
                sourcePrefab.transform,
                filter,
                chunkSet,
                interior);

            BindBreakable(sourcePrefabPath, shattered);
            ReportBake(
                sourceMesh,
                parts.Count,
                pieces,
                partsCut,
                partsCutByBlast,
                blast == null
                    ? "not built"
                    : blast.CutNoisy + " in noisy slices, " + blast.CutFlat + " in its voronoi cells, " +
                        blast.Refused + " refused",
                unclosedCuts,
                chunkSetPath,
                shatteredPrefabPath);
            return shattered;
        }

        /// <summary>
        /// Tells the model what it breaks into.
        /// </summary>
        /// <remarks>
        /// Bound here, by the builder that cut the pieces, because this is the
        /// one moment both halves are in hand: a rebake that produced different
        /// pieces cannot leave the prop pointing at the old ones, and nothing
        /// else in the client has to be told that a nest is breakable.
        /// </remarks>
        private static void BindBreakable(string sourcePrefabPath, GameObject shattered)
        {
            if (shattered == null)
            {
                return;
            }

            var sourceAsset = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePrefabPath);
            NetworkBreakableProp bound = sourceAsset == null
                ? null
                : sourceAsset.GetComponent<NetworkBreakableProp>();
            if (bound != null && bound.ShatteredModel == shattered)
            {
                return;
            }

            GameObject contents = PrefabUtility.LoadPrefabContents(sourcePrefabPath);
            try
            {
                NetworkBreakableProp breakable =
                    contents.GetComponent<NetworkBreakableProp>();
                if (breakable == null)
                {
                    breakable = contents.AddComponent<NetworkBreakableProp>();
                }

                breakable.Configure(shattered);
                PrefabUtility.SaveAsPrefabAsset(contents, sourcePrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>
        /// Puts the chunk meshes inside the chunk set asset, replacing whatever a
        /// previous bake left there.
        /// </summary>
        /// <remarks>
        /// The asset is reused rather than deleted and remade so its guid -- and
        /// so every reference to it -- survives a rebuild. The meshes inside it
        /// are not: they are destroyed and made again, because a bake that
        /// produced a different number of pieces has nothing to update in place.
        /// </remarks>
        private static ShatterChunkSet WriteChunkSet(
            string chunkSetPath,
            Mesh sourceMesh,
            List<MeshIsland> islands)
        {
            ShatterChunkSet chunkSet =
                AssetDatabase.LoadAssetAtPath<ShatterChunkSet>(chunkSetPath);
            if (chunkSet == null)
            {
                chunkSet = ScriptableObject.CreateInstance<ShatterChunkSet>();
                AssetDatabase.CreateAsset(chunkSet, chunkSetPath);
            }
            else
            {
                Object[] existing = AssetDatabase.LoadAllAssetsAtPath(chunkSetPath);
                for (int index = 0; index < existing.Length; ++index)
                {
                    if (existing[index] is Mesh stale)
                    {
                        Object.DestroyImmediate(stale, true);
                    }
                }
            }

            var chunks = new Mesh[islands.Count];
            var pivots = new Vector3[islands.Count];
            for (int index = 0; index < islands.Count; ++index)
            {
                chunks[index] = islands[index].CreateMesh(ChunkName(index));
                pivots[index] = islands[index].Pivot;
                AssetDatabase.AddObjectToAsset(chunks[index], chunkSet);
            }

            chunkSet.Configure(sourceMesh, chunks, pivots);
            EditorUtility.SetDirty(chunkSet);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(chunkSetPath);
            return chunkSet;
        }

        private static bool NeedsInterior(List<MeshIsland> pieces)
        {
            for (int index = 0; index < pieces.Count; ++index)
            {
                if (pieces[index].InteriorTriangles.Length > 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The material for faces a cut made, created the first time a bake
        /// needs one: the model's own shader with no texture and the colour of
        /// the inside of something broken.
        /// </summary>
        private static Material EnsureInteriorMaterial(string path, MeshRenderer sourceRenderer)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                return existing;
            }

            Material template = sourceRenderer != null ? sourceRenderer.sharedMaterial : null;
            var created = template != null
                ? new Material(template)
                : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            created.name = Path.GetFileNameWithoutExtension(path);
            created.mainTexture = null;
            created.color = new Color(0.36f, 0.21f, 0.12f);
            AssetDatabase.CreateAsset(created, path);
            return created;
        }

        private static GameObject WritePrefab(
            string shatteredPrefabPath,
            Transform sourceRoot,
            MeshFilter sourceFilter,
            ShatterChunkSet chunkSet,
            Material interior)
        {
            var root = new GameObject(
                Path.GetFileNameWithoutExtension(shatteredPrefabPath));
            Transform parent = RepeatChain(sourceFilter.transform, sourceRoot, root.transform);
            var sourceRenderer = sourceFilter.GetComponent<MeshRenderer>();
            Mesh[] chunks = chunkSet.Chunks;
            Vector3[] pivots = chunkSet.Pivots;
            for (int index = 0; index < chunks.Length; ++index)
            {
                var chunk = new GameObject(ChunkName(index));
                chunk.transform.SetParent(parent, false);
                chunk.transform.localPosition = pivots[index];
                chunk.AddComponent<MeshFilter>().sharedMesh = chunks[index];
                var renderer = chunk.AddComponent<MeshRenderer>();
                if (sourceRenderer != null)
                {
                    renderer.sharedMaterials = chunks[index].subMeshCount > 1 && interior != null
                        ? new[] { sourceRenderer.sharedMaterial, interior }
                        : sourceRenderer.sharedMaterials;
                    renderer.shadowCastingMode = sourceRenderer.shadowCastingMode;
                    renderer.receiveShadows = sourceRenderer.receiveShadows;
                    renderer.lightProbeUsage = sourceRenderer.lightProbeUsage;
                }
            }

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, shatteredPrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        /// <summary>
        /// Rebuilds the transforms between the source prefab's root and the node
        /// that drew the mesh, and returns the node the chunks go under.
        /// </summary>
        private static Transform RepeatChain(
            Transform sourceLeaf,
            Transform sourceRoot,
            Transform root)
        {
            var chain = new List<Transform>();
            for (Transform node = sourceLeaf; node != null && node != sourceRoot; node = node.parent)
            {
                chain.Add(node);
            }

            Transform parent = root;
            for (int index = chain.Count - 1; index >= 0; --index)
            {
                Transform source = chain[index];
                var node = new GameObject(source.name);
                node.transform.SetParent(parent, false);
                node.transform.localPosition = source.localPosition;
                node.transform.localRotation = source.localRotation;
                node.transform.localScale = source.localScale;
                parent = node.transform;
            }

            return parent;
        }

        /// <summary>
        /// States what the bake produced, and whether the pieces still add up to
        /// the model they came from.
        /// </summary>
        private static void ReportBake(
            Mesh sourceMesh,
            int partCount,
            List<MeshIsland> pieces,
            int partsCut,
            int partsCutByBlast,
            string blastSummary,
            int unclosedCuts,
            string chunkSetPath,
            string shatteredPrefabPath)
        {
            int triangles = 0;
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int index = 0; index < pieces.Count; ++index)
            {
                MeshIsland piece = pieces[index];
                triangles += piece.TriangleCount;
                min = Vector3.Min(min, piece.Pivot + piece.LocalBounds.min);
                max = Vector3.Max(max, piece.Pivot + piece.LocalBounds.max);
            }

            int sourceTriangles = sourceMesh.triangles.Length / 3;
            var baked = new Bounds();
            baked.SetMinMax(min, max);
            Debug.Log(
                "Shattered " + sourceMesh.name + ": " + partCount + " modelled parts, " +
                partsCut + " of them cut down (" + partsCutByBlast + " by Blast: " + blastSummary +
                "), " + pieces.Count + " pieces.\n" +
                triangles + " triangles against the model\'s " + sourceTriangles +
                " (+" + (triangles - sourceTriangles) + " for the cut faces), " +
                "largest piece " + pieces[0].TriangleCount + " triangles, smallest " +
                pieces[pieces.Count - 1].TriangleCount + ".\n" +
                unclosedCuts + " cuts ran off an edge the model already had and " +
                "were left open.\n" +
                "bounds " + baked + " against the model\'s " + sourceMesh.bounds + ".\n" +
                chunkSetPath + "\n" + shatteredPrefabPath);

            if (triangles < sourceTriangles)
            {
                Debug.LogError(
                    "The pieces hold " + triangles + " triangles and the model " +
                    sourceTriangles + ". A cut adds faces and never drops any, so " +
                    "the split has lost geometry.");
            }

            if (Vector3.Distance(baked.min, sourceMesh.bounds.min) > 0.001f ||
                Vector3.Distance(baked.max, sourceMesh.bounds.max) > 0.001f)
            {
                Debug.LogWarning(
                    "The pieces cover " + baked + " and the model " +
                    sourceMesh.bounds + ". They no longer fill the model\'s " +
                    "silhouette, so one of them has moved.");
            }
        }

        private static string ChunkName(int index)
        {
            return "chunk_" + index.ToString("D3");
        }
    }
}
