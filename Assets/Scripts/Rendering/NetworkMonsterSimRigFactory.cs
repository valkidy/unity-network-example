using System.Collections.Generic;
using NetworkExample.Kernel;
using NetworkExample.Kernel.Presentation;
using NetworkExample.UnityDemo.Common;
using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    /// <summary>
    /// Builds a presentation rig for the native <c>simplified_monster_sim_v4</c>
    /// skeleton straight from its manifest's bone name and parent tables, so
    /// LocomotionTest can verify the kernel pose without an imported FBX. The
    /// kernel repository only ships the rig as
    /// <c>game_server/skeleton_assets/raw/simplified_monster_sim_v4.glb</c>, which
    /// Unity cannot import without a glTF package.
    ///
    /// The manifest comes out of the gameplay catalog bundle
    /// (see <see cref="NetworkSkeletonManifests"/>), so the bundle has to be read
    /// before a rig is built -- which is also what makes the hierarchy match the
    /// skeleton the kernel actually loaded.
    ///
    /// The generated hierarchy carries the bone names and parent relationships
    /// <see cref="KernelSkeletonBinding.TryAutoMap"/> validates. Local transforms
    /// start at identity: with <c>PreservePrefabBindPose = false</c> the pose
    /// applicator overwrites position, rotation and scale from the native pose
    /// every frame, so no bind pose is needed on the Unity side.
    /// </summary>
    public static class NetworkMonsterSimRigFactory
    {
        public const float DefaultMarkerDiameter = 1.2f;
        public const float DefaultLinkThickness = 0.7f;

        /// <summary>
        /// Returns null and logs when no manifest is loaded for the skeleton.
        /// </summary>
        public static GameObject Create(string name)
        {
            return Create(name, DefaultMarkerDiameter, DefaultLinkThickness, null);
        }

        /// <summary>
        /// Returns null and logs when no manifest is loaded for the skeleton.
        /// </summary>
        public static GameObject Create(
            string name,
            float markerDiameter,
            float linkThickness,
            Material material)
        {
            GameObject rig = TryCreate(
                name,
                markerDiameter,
                linkThickness,
                material,
                out string error);
            if (rig == null)
            {
                Debug.LogError("MonsterSimRig could not be built: " + error);
            }
            return rig;
        }

        public static GameObject TryCreate(
            string name,
            float markerDiameter,
            float linkThickness,
            Material material,
            out string error)
        {
            if (!NetworkSkeletonManifests.TryGetMonsterSim(
                    out KernelSkeletonManifest manifest,
                    out error))
            {
                return null;
            }

            var rig = new GameObject(string.IsNullOrEmpty(name) ? "MonsterSimRig" : name);
            int boneCount = manifest.BoneCount;
            var bones = new Transform[boneCount];

            for (int index = 0; index < boneCount; ++index)
            {
                KernelSkeletonManifestBone manifestBone = manifest.Bones[index];
                var bone = new GameObject(manifestBone.Name);
                int parentIndex = manifestBone.ParentIndex;
                bone.transform.SetParent(
                    parentIndex >= 0 ? bones[parentIndex] : rig.transform,
                    false);
                bones[index] = bone.transform;
            }

            if (markerDiameter > 0f)
            {
                for (int index = 0; index < boneCount; ++index)
                {
                    AddMarker(bones[index], markerDiameter, material);
                }
            }

            KernelSkeletonBinding binding = rig.AddComponent<KernelSkeletonBinding>();
            binding.SkeletonAssetId = manifest.AssetId;
            binding.SkeletonContentHash = manifest.ContentHash;
            binding.SkeletonRoot = rig.transform;
            binding.AutoMapKnownSkeleton = true;
            // The generated hierarchy has no bind pose of its own, so the applicator
            // must write the native local transforms directly. This also keeps the
            // Unity pose identical to the one the native capture records.
            binding.PreservePrefabBindPose = false;
            binding.Bones = bones;

            rig.AddComponent<KernelSkeletonPoseApplicator>();

            if (linkThickness > 0f)
            {
                var linkRoot = new GameObject("Links");
                linkRoot.transform.SetParent(rig.transform, false);
                CollectLinkPairs(
                    manifest,
                    bones,
                    out Transform[] parents,
                    out Transform[] children);
                rig.AddComponent<NetworkSkeletonLinkView>().Configure(
                    linkRoot.transform,
                    parents,
                    children,
                    linkThickness,
                    material);
            }

            error = null;
            return rig;
        }

        private static void AddMarker(Transform bone, float diameter, Material material)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            // "Marker" is not a skeleton bone name, so auto-mapping ignores it.
            marker.name = "Marker";
            DestroyComponent(marker.GetComponent<Collider>());
            if (material != null)
            {
                marker.GetComponent<MeshRenderer>().sharedMaterial = material;
            }
            marker.transform.SetParent(bone, false);
            marker.transform.localScale = new Vector3(diameter, diameter, diameter);
        }

        /// <summary>
        /// The rig is also built outside play mode by editor tooling, where
        /// <see cref="Object.Destroy"/> is not allowed.
        /// </summary>
        internal static void DestroyComponent(Component component)
        {
            if (component == null)
            {
                return;
            }
            if (Application.isPlaying)
            {
                Object.Destroy(component);
            }
            else
            {
                Object.DestroyImmediate(component);
            }
        }

        private static void CollectLinkPairs(
            KernelSkeletonManifest manifest,
            Transform[] bones,
            out Transform[] parents,
            out Transform[] children)
        {
            var parentList = new List<Transform>(bones.Length);
            var childList = new List<Transform>(bones.Length);
            for (int index = 0; index < bones.Length; ++index)
            {
                int parentIndex = manifest.Bones[index].ParentIndex;
                if (parentIndex < 0)
                {
                    continue;
                }
                parentList.Add(bones[parentIndex]);
                childList.Add(bones[index]);
            }

            parents = parentList.ToArray();
            children = childList.ToArray();
        }
    }
}
