using System.Collections.Generic;
using System.IO;
using System.Text;
using NetworkExample.Kernel;
using NetworkExample.Kernel.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace NetworkExample.UnityDemo.EditorTools
{
    /// <summary>
    /// Bakes the glTF skeleton sources into kernel-drivable actor prefabs.
    ///
    /// Import the .glb through glTFast, not a Blender-converted .fbx. The .glb is
    /// the same file the kernel's .ozz runtime skeleton is generated from, so
    /// glTFast reproduces it node for node: all 41 bones present, parents
    /// matching the skeleton manifest, bind rotations identical to the ozz bind
    /// pose (worst delta 0.0000 degrees), and SKIN_BindCarrier still a skinned
    /// mesh under SIM_Root.
    ///
    /// The Blender .fbx round-trip damaged all four of those, and the result was
    /// measurably wrong locomotion -- against
    /// capture/locomotion_tests/native_raw_bones.csv it drifted 9.4 m per bone on
    /// average, versus 0.0 m for the .glb:
    ///
    ///   - an extra "world" node above SIM_Root carrying the -90 degree X axis
    ///     conversion, which lands on top of every bone. This is the dominant
    ///     error and KernelSkeletonBinding cannot catch it: SIM_Root is the
    ///     skeleton root, so its parent is never validated.
    ///   - the three locator bones LOC_FrontArc, LOC_Com and LOC_Mouth dropped.
    ///   - SKIN_BindCarrier reparented to the file root and demoted from a
    ///     skinned mesh to a static one.
    ///   - every GEO_* node rotated by 90 degrees.
    ///
    /// The repair steps below are no-ops for the .glb and are kept as a guard so
    /// a future source that reintroduces any of those faults is corrected rather
    /// than silently mis-posed.
    ///
    /// rock_robot_biped_24u_v3 (skeleton asset 2, 18 bones)
    ///   - A flat list of geometry nodes with no joint hierarchy, exactly as the
    ///     manifest describes, so the bone array is filled in manifest order.
    ///     KernelSkeletonBinding has no built-in profile for this asset, so
    ///     auto-mapping is switched off and the array is assigned explicitly.
    /// </summary>
    public static class NetworkActorRigPrefabBuilder
    {
        // The glTF sources are what the kernel's .ozz runtime skeletons are built
        // from, so glTFast reproduces the skeleton exactly: same 41 nodes, same
        // parents, same bind rotations. The Blender-converted .fbx does not -- it
        // inserts a "world" axis-conversion node above SIM_Root, drops the three
        // LOC_* locators, reparents SKIN_BindCarrier to the file root and demotes
        // it from a skinned mesh, and rotates every GEO_* node by 90 degrees.
        public const string MonsterModelPath =
            "Assets/Resources/Actors/simplified_monster_sim_v4.glb";
        public const string RockRobotModelPath =
            "Assets/Resources/Actors/rock_robot_biped_24u_v3.glb";
        public const string MonsterPrefabPath =
            "Assets/Presentation/Prefabs/Actor_MonsterSim.prefab";
        public const string RockRobotPrefabPath =
            "Assets/Presentation/Prefabs/Actor_RockRobot.prefab";
        public const string RigMaterialPath =
            "Assets/Presentation/Materials/ActorRigPlaceholder.mat";

        public const uint RockRobotSkeletonAssetId = 2u;
        public const ulong RockRobotSkeletonContentHash = 0xc7e5f01249f761e0UL;

        /// <summary>Bone order of rock_robot_biped_24u_v3.skeleton_manifest.json.</summary>
        private static readonly string[] RockRobotBoneNames =
        {
            "GEO_ArmLeft_Elbow_Node",
            "GEO_ArmLeft_Lower_Node",
            "GEO_ArmLeft_Upper_Node",
            "GEO_ArmLeft_Wrist_Node",
            "GEO_ArmRight_Elbow_Node",
            "GEO_ArmRight_Lower_Node",
            "GEO_ArmRight_Upper_Node",
            "GEO_ArmRight_Wrist_Node",
            "GEO_EyeOrb_Node",
            "GEO_Head_Node",
            "GEO_LegLeft_Foot_Node",
            "GEO_LegLeft_Lower_Node",
            "GEO_LegLeft_Upper_Node",
            "GEO_LegRight_Foot_Node",
            "GEO_LegRight_Lower_Node",
            "GEO_LegRight_Upper_Node",
            "GEO_PelvisBar_Node",
            "GEO_Torso_Node",
        };

        [MenuItem("Tools/Network Example/Build Actor Rig Prefabs")]
        public static void Build()
        {
            var report = new StringBuilder();
            report.AppendLine("Actor rig prefabs");
            report.AppendLine(BuildMonsterPrefab());
            report.AppendLine(BuildRockRobotPrefab());
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(report.ToString());
        }

        public static void BuildBatch()
        {
            Build();
            EditorApplication.Exit(0);
        }

        private static string BuildMonsterPrefab()
        {
            if (!TryInstantiate(MonsterModelPath, out GameObject instance, out string error))
            {
                return "  [FAIL] " + error;
            }

            try
            {
                instance.name = Path.GetFileNameWithoutExtension(MonsterPrefabPath);
                Dictionary<string, Transform> byName = IndexByName(instance);

                // Guard: drop any importer axis-conversion node from the skeleton
                // chain. The kernel poses SIM_Root relative to the entity root, so
                // a transform sitting between the two is applied twice.
                if (byName.TryGetValue("SIM_Root", out Transform skeletonRoot) &&
                    skeletonRoot.parent != instance.transform)
                {
                    skeletonRoot.SetParent(instance.transform, false);
                }

                // Guard: the runtime skeleton parents the skin carrier under
                // SIM_Root.
                if (byName.TryGetValue("SKIN_BindCarrier", out Transform skinCarrier) &&
                    byName.TryGetValue("SIM_Root", out Transform simRoot) &&
                    skinCarrier.parent != simRoot)
                {
                    skinCarrier.SetParent(simRoot, false);
                }

                // Guard: recreate any locator bone the source model dropped.
                // KernelSkeletonBinding requires all 41 transforms.
                int recreated = 0;
                for (int index = 0;
                     index < KernelSkeletonBinding.DefaultBoneCount;
                     ++index)
                {
                    string boneName = KernelSkeletonBinding.GetDefaultBoneName(index);
                    if (byName.ContainsKey(boneName))
                    {
                        continue;
                    }

                    int parentIndex =
                        KernelSkeletonBinding.GetDefaultParentBoneIndex(index);
                    string parentName = parentIndex >= 0
                        ? KernelSkeletonBinding.GetDefaultBoneName(parentIndex)
                        : null;
                    if (parentName == null ||
                        !byName.TryGetValue(parentName, out Transform parent))
                    {
                        return "  [FAIL] cannot recreate '" + boneName +
                            "': parent '" + parentName + "' is missing from the source model";
                    }

                    var locator = new GameObject(boneName);
                    locator.transform.SetParent(parent, false);
                    byName[boneName] = locator.transform;
                    ++recreated;
                }

                var bones = new Transform[KernelSkeletonBinding.DefaultBoneCount];
                for (int index = 0; index < bones.Length; ++index)
                {
                    bones[index] = byName[
                        KernelSkeletonBinding.GetDefaultBoneName(index)];
                }

                KernelSkeletonBinding binding =
                    instance.AddComponent<KernelSkeletonBinding>();
                binding.SkeletonAssetId = KernelSkeletonBinding.DefaultSkeletonAssetId;
                binding.SkeletonContentHash =
                    KernelSkeletonBinding.DefaultSkeletonContentHash;
                binding.SkeletonRoot = instance.transform;
                binding.AutoMapKnownSkeleton = true;
                // Overwrite, do not delta. The FBX bind pose does not agree with the
                // ozz runtime skeleton it is supposed to mirror -- joint X is negated
                // and the GEO_* mesh nodes carry an extra 90 degree rotation -- so
                // applying the kernel pose as a delta on top of it lands 9.4 m off
                // per bone. Writing the native locals verbatim reproduces the native
                // capture exactly (0.0 m).
                binding.PreservePrefabBindPose = false;
                binding.Bones = bones;
                instance.AddComponent<KernelSkeletonPoseApplicator>();

                if (!binding.TryValidate(out string bindingError))
                {
                    return "  [FAIL] " + MonsterPrefabPath + ": " + bindingError;
                }

                var notes = new StringBuilder();
                int unmirrored = UnmirrorGeometry(instance, notes);
                int materialized = AssignPlaceholderMaterial(instance);
                SavePrefab(instance, MonsterPrefabPath);
                return "  [OK]   " + MonsterPrefabPath + " (" + bones.Length +
                    " bones, " + recreated + " locator(s) recreated, " +
                    unmirrored + " mesh(es) un-mirrored, " +
                    materialized + " renderer(s) given a placeholder material" +
                    notes + ")";
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        private static string BuildRockRobotPrefab()
        {
            if (!TryInstantiate(RockRobotModelPath, out GameObject instance, out string error))
            {
                return "  [FAIL] " + error;
            }

            try
            {
                instance.name = Path.GetFileNameWithoutExtension(RockRobotPrefabPath);
                Dictionary<string, Transform> byName = IndexByName(instance);

                var bones = new Transform[RockRobotBoneNames.Length];
                for (int index = 0; index < bones.Length; ++index)
                {
                    if (!byName.TryGetValue(RockRobotBoneNames[index], out bones[index]))
                    {
                        return "  [FAIL] " + RockRobotPrefabPath + ": bone '" +
                            RockRobotBoneNames[index] + "' is missing from the source model";
                    }
                }

                KernelSkeletonBinding binding =
                    instance.AddComponent<KernelSkeletonBinding>();
                binding.SkeletonAssetId = RockRobotSkeletonAssetId;
                binding.SkeletonContentHash = RockRobotSkeletonContentHash;
                binding.SkeletonRoot = instance.transform;
                // KernelSkeletonBinding only ships a bone profile for
                // simplified_monster_sim_v4, so this array cannot be auto-mapped.
                binding.AutoMapKnownSkeleton = false;
                // Same reasoning as the monster rig: the kernel pose is authoritative.
                binding.PreservePrefabBindPose = false;
                binding.Bones = bones;
                instance.AddComponent<KernelSkeletonPoseApplicator>();

                if (!binding.TryValidate(out string bindingError))
                {
                    return "  [FAIL] " + RockRobotPrefabPath + ": " + bindingError;
                }

                var notes = new StringBuilder();
                int unmirrored = UnmirrorGeometry(instance, notes);
                int materialized = AssignPlaceholderMaterial(instance);
                SavePrefab(instance, RockRobotPrefabPath);
                return "  [OK]   " + RockRobotPrefabPath + " (" + bones.Length +
                    " bones, skeleton asset " + RockRobotSkeletonAssetId + ", " +
                    unmirrored + " mesh(es) un-mirrored, " +
                    materialized + " renderer(s) given a placeholder material" +
                    notes + "; no actor template maps to it yet)";
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        private static bool TryInstantiate(
            string fbxPath,
            out GameObject instance,
            out string error)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (source == null)
            {
                instance = null;
                error = fbxPath + " was not found";
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
        /// exactly. The child is not part of the 41-bone binding, so the applicator
        /// never touches it.
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
