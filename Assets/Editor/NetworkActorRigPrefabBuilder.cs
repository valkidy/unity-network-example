using System.Collections.Generic;
using System.IO;
using System.Text;
using NetworkExample.Kernel;
using NetworkExample.Kernel.Presentation;
using NetworkExample.UnityDemo.Common;
using NetworkExample.UnityDemo.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace NetworkExample.UnityDemo.EditorTools
{
    /// <summary>
    /// Bakes the glTF skeleton sources into kernel-drivable actor prefabs.
    ///
    /// Every rig is built the same way, from the skeleton manifest in the
    /// gameplay catalog bundle: the manifest states the bone order, the bone
    /// names and the parent of each bone, and the prefab is made to match it.
    /// Nothing here knows a rig by name, so adding a skeleton to the catalog and
    /// its .glb to <see cref="Rigs"/> is all a new actor prefab needs.
    ///
    /// Import the .glb through glTFast, not a Blender-converted .fbx. The .glb is
    /// the same file the kernel's .ozz runtime skeleton is generated from, so
    /// glTFast reproduces it node for node: every manifest bone present, parents
    /// matching the manifest, and bind rotations identical to the ozz bind pose
    /// (worst delta 0.0000 degrees on simplified_monster_sim_v4).
    ///
    /// The Blender .fbx round-trip damaged all of that, and the result was
    /// measurably wrong locomotion -- against
    /// capture/locomotion_tests/native_raw_bones.csv it drifted 9.4 m per bone on
    /// average, versus 0.0 m for the .glb:
    ///
    ///   - an extra axis-conversion node above the skeleton root carrying the
    ///     -90 degree X conversion, which lands on top of every bone. This is the
    ///     dominant error and KernelSkeletonBinding cannot catch it: the parent
    ///     of the manifest's root bone is never validated.
    ///   - the three locator bones LOC_FrontArc, LOC_Com and LOC_Mouth dropped.
    ///   - SKIN_BindCarrier reparented to the file root and demoted from a
    ///     skinned mesh to a static one.
    ///   - every GEO_* node rotated by 90 degrees.
    ///
    /// The repair steps below are no-ops for a .glb and are kept as a guard so a
    /// future source that reintroduces any of those faults is corrected rather
    /// than silently mis-posed. They are stated against the manifest rather than
    /// against one rig's bone names: a node that the manifest does not list is
    /// left where it is, a listed bone is reparented to the bone the manifest
    /// says is its parent, and a listed bone the source dropped is recreated as
    /// an empty locator.
    ///
    /// Note that the template rigs exported from LocomotionModel.unity carry
    /// their scene nodes -- "world" and the rig's own name -- as manifest bones
    /// above SIM_Root, where simplified_monster_sim_v4 starts at SIM_Root. Both
    /// are correct; the manifest is what decides.
    /// </summary>
    public static class NetworkActorRigPrefabBuilder
    {
        /// <summary>One rig's .glb source and the prefab baked from it.</summary>
        public struct RigSource
        {
            public string ModelPath;
            public string PrefabPath;
            public uint SkeletonAssetId;

            /// <summary>
            /// The manifest name expected at <see cref="SkeletonAssetId"/>,
            /// so a recatalogued asset id fails the build instead of baking the
            /// wrong skeleton into the prefab.
            /// </summary>
            public string SkeletonName;
        }

        public const string RigMaterialPath =
            "Assets/Presentation/Materials/ActorRigPlaceholder.mat";

        private const string ModelDirectory = "Assets/Resources/Actors/";
        private const string PrefabDirectory = "Assets/Presentation/Prefabs/";
        private const string CatalogPath = "Assets/Resources/NetworkPrefabCatalog.asset";

        public const string MonsterPrefabPath = PrefabDirectory + "Actor_MonsterSim.prefab";
        public const string RockRobotPrefabPath = PrefabDirectory + "Actor_RockRobot.prefab";
        public const string BipedPrefabPath = PrefabDirectory + "Actor_Biped.prefab";
        public const string QuadrupedPrefabPath = PrefabDirectory + "Actor_Quadruped.prefab";
        public const string TripodPrefabPath = PrefabDirectory + "Actor_Tripod.prefab";

        /// <summary>
        /// The rigs this project bakes, paired with the skeleton asset each one
        /// is registered as in the gameplay catalog
        /// (game_server/skeleton_assets/BUILD.bazel).
        /// </summary>
        public static readonly RigSource[] Rigs =
        {
            new RigSource
            {
                // Kept as the control: the only rig with a locomotion capture
                // golden proving the system unchanged.
                ModelPath = ModelDirectory + "simplified_monster_sim_v4.glb",
                PrefabPath = MonsterPrefabPath,
                SkeletonAssetId = 1u,
                SkeletonName = "simplified_monster_sim_v4",
            },
            new RigSource
            {
                ModelPath = ModelDirectory + "rock_robot_biped_24u_v3.glb",
                PrefabPath = RockRobotPrefabPath,
                SkeletonAssetId = 2u,
                SkeletonName = "rock_robot_biped_24u_v3",
            },
            new RigSource
            {
                ModelPath = ModelDirectory + "simplified_biped.glb",
                PrefabPath = BipedPrefabPath,
                SkeletonAssetId = 3u,
                SkeletonName = "simplified_biped",
            },
            new RigSource
            {
                ModelPath = ModelDirectory + "simplified_quadruped.glb",
                PrefabPath = QuadrupedPrefabPath,
                SkeletonAssetId = 4u,
                SkeletonName = "simplified_quadruped",
            },
            new RigSource
            {
                ModelPath = ModelDirectory + "simplified_tripod.glb",
                PrefabPath = TripodPrefabPath,
                SkeletonAssetId = 5u,
                SkeletonName = "simplified_tripod",
            },
        };

        [MenuItem("Tools/Network Example/Build Actor Rig Prefabs")]
        public static void Build()
        {
            var report = new StringBuilder();
            report.AppendLine("Actor rig prefabs");
            var prefabPathBySkeletonAssetId = new Dictionary<uint, string>();
            for (int index = 0; index < Rigs.Length; ++index)
            {
                string line = BuildPrefab(Rigs[index]);
                report.AppendLine(line);
                if (line.Contains("[OK]"))
                {
                    prefabPathBySkeletonAssetId[Rigs[index].SkeletonAssetId] =
                        Rigs[index].PrefabPath;
                }
            }
            report.AppendLine(RegisterCatalogBindings(prefabPathBySkeletonAssetId));
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(report.ToString());
        }

        public static void BuildBatch()
        {
            Build();
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// Points every rigged entity template at the prefab baked from its own
        /// skeleton.
        /// </summary>
        /// <remarks>
        /// Which template uses which skeleton is read out of the bundle rather
        /// than written down here, so the pairing cannot drift from the server's:
        /// the template names the manifest, the manifest names the skeleton
        /// asset, and the rig baked from that asset is what the template draws.
        /// Unrelated bindings are left alone.
        /// </remarks>
        private static string RegisterCatalogBindings(
            Dictionary<uint, string> prefabPathBySkeletonAssetId)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<NetworkPrefabCatalog>(CatalogPath);
            if (catalog == null)
            {
                return "  [SKIP] " + CatalogPath + " not found; no actor bindings written";
            }

            var bindings = new List<NetworkPrefabCatalog.ActorPrefabBinding>();
            var described = new List<string>();
            foreach (KeyValuePair<uint, uint> pair in
                     NetworkSkeletonManifests.SkeletonAssetIdByTemplate)
            {
                if (!prefabPathBySkeletonAssetId.TryGetValue(
                        pair.Value,
                        out string prefabPath))
                {
                    continue;
                }
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null)
                {
                    continue;
                }
                bindings.Add(
                    new NetworkPrefabCatalog.ActorPrefabBinding(pair.Key, prefab));
                described.Add(pair.Key + " -> " + Path.GetFileNameWithoutExtension(prefabPath));
            }

            if (bindings.Count == 0)
            {
                return "  [SKIP] no entity template in the catalog is rigged to a " +
                    "baked prefab";
            }

            described.Sort();
            catalog.Configure(
                UpsertActorBindings(catalog.ActorPrefabs, bindings.ToArray()),
                catalog.ProjectilePrefabs,
                catalog.PlayerActorFallback,
                catalog.AgentActorFallback,
                catalog.ProjectileFallback,
                catalog.EntityFallback);
            EditorUtility.SetDirty(catalog);
            return "  [OK]   " + CatalogPath + " (" + string.Join(", ", described) + ")";
        }

        private static NetworkPrefabCatalog.ActorPrefabBinding[] UpsertActorBindings(
            NetworkPrefabCatalog.ActorPrefabBinding[] source,
            NetworkPrefabCatalog.ActorPrefabBinding[] replacements)
        {
            var replacedIds = new HashSet<uint>();
            for (int index = 0; index < replacements.Length; ++index)
            {
                replacedIds.Add(replacements[index].actorTemplateId);
            }

            var result = new List<NetworkPrefabCatalog.ActorPrefabBinding>();
            if (source != null)
            {
                for (int index = 0; index < source.Length; ++index)
                {
                    if (!replacedIds.Contains(source[index].actorTemplateId))
                    {
                        result.Add(source[index]);
                    }
                }
            }

            result.AddRange(replacements);
            result.Sort(
                (left, right) => left.actorTemplateId.CompareTo(right.actorTemplateId));
            return result.ToArray();
        }

        private static string BuildPrefab(RigSource rig)
        {
            if (!NetworkSkeletonManifests.TryGet(
                    rig.SkeletonAssetId,
                    rig.SkeletonName,
                    out KernelSkeletonManifest manifest,
                    out string manifestError))
            {
                return "  [FAIL] " + rig.PrefabPath + ": " + manifestError;
            }
            if (!TryInstantiate(rig.ModelPath, out GameObject instance, out string error))
            {
                return "  [FAIL] " + error;
            }

            try
            {
                instance.name = Path.GetFileNameWithoutExtension(rig.PrefabPath);
                Dictionary<string, Transform> byName = IndexByName(instance);

                int recreated = RecreateMissingBones(
                    manifest,
                    instance,
                    byName,
                    out string recreateError);
                if (recreateError != null)
                {
                    return "  [FAIL] " + rig.PrefabPath + ": " + recreateError;
                }

                int reparented = RepairHierarchy(manifest, instance, byName);

                var bones = new Transform[manifest.BoneCount];
                for (int index = 0; index < bones.Length; ++index)
                {
                    bones[index] = byName[manifest.Bones[index].Name];
                }

                KernelSkeletonBinding binding =
                    instance.AddComponent<KernelSkeletonBinding>();
                binding.SkeletonAssetId = manifest.AssetId;
                binding.SkeletonContentHash = manifest.ContentHash;
                binding.SkeletonRoot = instance.transform;
                // The array is assigned in manifest order right here, so mapping
                // by name would only repeat work already done; leaving it on also
                // lets OnValidate rewrite the array from a newer manifest.
                binding.AutoMapKnownSkeleton = true;
                // Overwrite, do not delta. An imported bind pose does not have to
                // agree with the ozz runtime skeleton it mirrors -- on the monster
                // .fbx joint X was negated and the GEO_* mesh nodes carried an
                // extra 90 degree rotation, which lands 9.4 m off per bone when
                // applied as a delta. Writing the native locals verbatim
                // reproduces the native capture exactly (0.0 m).
                binding.PreservePrefabBindPose = false;
                binding.Bones = bones;
                instance.AddComponent<KernelSkeletonPoseApplicator>();

                if (!binding.TryValidate(out string bindingError))
                {
                    return "  [FAIL] " + rig.PrefabPath + ": " + bindingError;
                }

                var notes = new StringBuilder();
                int unmirrored = UnmirrorGeometry(instance, notes);
                int materialized = AssignPlaceholderMaterial(instance);
                SavePrefab(instance, rig.PrefabPath);
                return "  [OK]   " + rig.PrefabPath + " (" + manifest.Name +
                    ", skeleton asset " + manifest.AssetId + ", " + bones.Length +
                    " bones, " + recreated + " locator(s) recreated, " +
                    reparented + " bone(s) reparented, " +
                    unmirrored + " mesh(es) un-mirrored, " +
                    materialized + " renderer(s) given a placeholder material" +
                    notes + ")";
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Recreates any manifest bone the source model dropped as an empty
        /// locator, because KernelSkeletonBinding requires every one of them.
        /// </summary>
        private static int RecreateMissingBones(
            KernelSkeletonManifest manifest,
            GameObject instance,
            Dictionary<string, Transform> byName,
            out string error)
        {
            error = null;
            int recreated = 0;
            for (int index = 0; index < manifest.BoneCount; ++index)
            {
                string boneName = manifest.Bones[index].Name;
                if (byName.ContainsKey(boneName))
                {
                    continue;
                }

                int parentIndex = manifest.Bones[index].ParentIndex;
                Transform parent = instance.transform;
                if (parentIndex >= 0 &&
                    !byName.TryGetValue(manifest.Bones[parentIndex].Name, out parent))
                {
                    error = "cannot recreate '" + boneName + "': parent '" +
                        manifest.Bones[parentIndex].Name +
                        "' is missing from the source model";
                    return recreated;
                }

                var locator = new GameObject(boneName);
                locator.transform.SetParent(parent, false);
                byName[boneName] = locator.transform;
                ++recreated;
            }
            return recreated;
        }

        /// <summary>
        /// Puts every manifest bone under the parent the manifest gives it, and
        /// the manifest's root bone directly under the prefab root.
        /// </summary>
        /// <remarks>
        /// This is the one structural rule the kernel cares about, and it covers
        /// what used to be two rig-specific guards: an importer node wedged above
        /// the skeleton root (applied twice, because the kernel poses the root
        /// relative to the entity), and a skin carrier the importer moved out of
        /// the skeleton.
        /// </remarks>
        private static int RepairHierarchy(
            KernelSkeletonManifest manifest,
            GameObject instance,
            Dictionary<string, Transform> byName)
        {
            int reparented = 0;
            for (int index = 0; index < manifest.BoneCount; ++index)
            {
                Transform bone = byName[manifest.Bones[index].Name];
                int parentIndex = manifest.Bones[index].ParentIndex;
                Transform parent = parentIndex >= 0
                    ? byName[manifest.Bones[parentIndex].Name]
                    : instance.transform;
                if (bone.parent == parent)
                {
                    continue;
                }
                bone.SetParent(parent, false);
                ++reparented;
            }
            return reparented;
        }

        private static bool TryInstantiate(
            string modelPath,
            out GameObject instance,
            out string error)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (source == null)
            {
                instance = null;
                error = modelPath + " was not found";
                return false;
            }

            instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
            PrefabUtility.UnpackPrefabInstance(
                instance,
                PrefabUnpackMode.Completely,
                InteractionMode.AutomatedAction);
            error = null;
            return true;
        }

        /// <summary>
        /// The skeleton glTF sources carry geometry only -- they declare no
        /// materials at all -- so glTFast leaves every renderer's material slot
        /// empty and the rig is present in the hierarchy but never drawn. Give
        /// every unassigned slot a placeholder so the rig is visible.
        /// </summary>
        private static int AssignPlaceholderMaterial(GameObject instance)
        {
            Material material = EnsureRigMaterial();
            int assigned = 0;
            foreach (Renderer renderer in
                     instance.GetComponentsInChildren<Renderer>(true))
            {
                Material[] materials = renderer.sharedMaterials;
                bool changed = false;
                if (materials == null || materials.Length == 0)
                {
                    materials = new[] { material };
                    changed = true;
                }
                else
                {
                    for (int index = 0; index < materials.Length; ++index)
                    {
                        if (materials[index] == null)
                        {
                            materials[index] = material;
                            changed = true;
                        }
                    }
                }
                if (changed)
                {
                    renderer.sharedMaterials = materials;
                    ++assigned;
                }
            }
            return assigned;
        }

        private static Material EnsureRigMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(RigMaterialPath);
            if (existing != null)
            {
                return existing;
            }

            // Pick the shader the project's *active* pipeline can actually render.
            // This project ships the URP package and URP global settings but has no
            // UniversalRenderPipelineAsset assigned in Graphics settings, so it runs
            // the built-in pipeline -- where a "Universal Render Pipeline/Lit"
            // material draws magenta. Assign a URP asset and this picks URP/Lit
            // instead; leave it unassigned and Standard is the correct choice.
            bool scriptableRenderPipeline =
                GraphicsSettings.currentRenderPipeline != null;
            Shader shader = scriptableRenderPipeline
                ? Shader.Find("Universal Render Pipeline/Lit")
                : Shader.Find("Standard");
            if (shader == null)
            {
                shader = Shader.Find("Standard") ??
                    Shader.Find("Universal Render Pipeline/Lit");
            }

            string directory = Path.GetDirectoryName(RigMaterialPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                AssetDatabase.Refresh();
            }

            var material = new Material(shader) { color = new Color(0.55f, 0.58f, 0.62f) };
            AssetDatabase.CreateAsset(material, RigMaterialPath);
            return material;
        }

        /// <summary>
        /// Un-mirrors the imported geometry so it lines up with the kernel pose.
        ///
        /// glTF is right-handed and Unity is left-handed, so glTFast negates X on
        /// both node transforms and mesh vertices -- a self-consistent mirror that
        /// looks identical on import. The kernel's ozz skeleton instead keeps the
        /// raw glTF values (JNT_LegRearRight_Hip is +4.2 in the .glb and in ozz,
        /// but -4.2 after glTFast). Because KernelSkeletonPoseApplicator overwrites
        /// every bone's local transform with the native pose, the joints end up in
        /// glTF space while the meshes are still mirrored, and the rig renders
        /// inside out with limbs pointing the wrong way.
        ///
        /// glTFast's conversion is per-node-local (v_unity = M * v_gltf with
        /// M = diag(-1,1,1)), so re-applying M on a child of each bone cancels it
        /// exactly. The child is not a manifest bone, so the applicator never
        /// touches it.
        /// </summary>
        private static int UnmirrorGeometry(GameObject instance, StringBuilder notes)
        {
            int moved = 0;
            var filters = new List<MeshFilter>(
                instance.GetComponentsInChildren<MeshFilter>(true));
            foreach (MeshFilter filter in filters)
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null)
                {
                    continue;
                }

                var holder = new GameObject("Mesh");
                holder.transform.SetParent(filter.transform, false);
                holder.transform.localScale = new Vector3(-1f, 1f, 1f);
                holder.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                holder.AddComponent<MeshRenderer>().sharedMaterials =
                    renderer.sharedMaterials;

                Object.DestroyImmediate(renderer);
                Object.DestroyImmediate(filter);
                ++moved;
            }

            // SKIN_BindCarrier is a skinned quad whose bind matrices live in
            // glTFast's mirrored space; a child transform cannot cancel that the
            // way it can for rigid meshes. It is a rigging artifact rather than
            // visible geometry -- the body is drawn by the GEO_* parts -- so it is
            // dropped instead of rendered wrong.
            foreach (SkinnedMeshRenderer skinned in
                     instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                notes.Append(", dropped skinned '").Append(skinned.name).Append('\'');
                Object.DestroyImmediate(skinned);
            }

            return moved;
        }

        private static Dictionary<string, Transform> IndexByName(GameObject instance)
        {
            var byName = new Dictionary<string, Transform>();
            foreach (Transform child in instance.GetComponentsInChildren<Transform>(true))
            {
                byName[child.name] = child;
            }
            return byName;
        }

        private static void SavePrefab(GameObject instance, string path)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                AssetDatabase.Refresh();
            }
            PrefabUtility.SaveAsPrefabAsset(instance, path);
        }
    }
}
