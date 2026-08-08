using System.Collections.Generic;
using NetworkExample.Kernel;
using NetworkExample.Kernel.Presentation;
using NetworkExample.UnityDemo.Common;
using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    /// <summary>
    /// Builds a presentation rig for a native skeleton straight from its
    /// manifest's bone name and parent tables, so a scene can verify the kernel
    /// pose without an imported model. The kernel repository ships the rigs as
    /// glTF under <c>game_server/skeleton_assets/raw/</c>, and the manifest is
    /// the only place the bone layout is stated in a form the client can read.
    ///
    /// The manifest comes out of the gameplay catalog bundle the server sent
    /// (see <see cref="NetworkSkeletonManifests"/>), so the bundle has to be read
    /// before a rig is built -- which is also what makes the hierarchy match the
    /// skeleton the kernel actually loaded, for any rig in the catalog rather
    /// than one the Unity side knows by name.
    ///
    /// The generated hierarchy carries the bone names and parent relationships
    /// <see cref="KernelSkeletonBinding.TryAutoMap"/> validates. Local transforms
    /// start at identity: with <c>PreservePrefabBindPose = false</c> the pose
    /// applicator overwrites position, rotation and scale from the native pose
    /// every frame, so no bind pose is needed on the Unity side.
    /// </summary>
    public static class NetworkSkeletonRigFactory
    {
        public const float DefaultMarkerDiameter = 1.2f;
        public const float DefaultLinkThickness = 0.7f;

        /// <summary>
        /// Builds the rig for whichever skeleton <paramref name="templateId"/> is
        /// rigged to in the loaded catalog. Returns null and logs when the
        /// template carries no skeleton or its manifest is missing.
        /// </summary>
        public static GameObject CreateForTemplate(
            uint templateId,
            float markerDiameter,
            float linkThickness,
            Material material)
        {
            GameObject rig = TryCreateForTemplate(
                templateId,
                markerDiameter,
                linkThickness,
                material,
                out string error);
            if (rig == null)
            {
                Debug.LogError(
                    "No rig could be built for entity template " + templateId + ": " +
                    error);
            }
            return rig;
        }

        public static GameObject TryCreateForTemplate(
            uint templateId,
            float markerDiameter,
            float linkThickness,
            Material material,
            out string error)
        {
            if (!NetworkSkeletonManifests.TryGetForTemplate(
                    templateId,
                    out KernelSkeletonManifest manifest,
                    out error))
            {
                return null;
            }
            return TryCreate(manifest, null, markerDiameter, linkThickness, material, out error);
        }

        public static GameObject TryCreate(
            KernelSkeletonManifest manifest,
            string name,
            float markerDiameter,
            float linkThickness,
            Material material,
            out string error)
        {
            if (manifest == null || manifest.BoneCount == 0)
            {
                error = "Skeleton manifest is empty.";
                return null;
            }

            var rig = new GameObject(
                string.IsNullOrEmpty(name) ? manifest.Name + "_rig" : name);
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
