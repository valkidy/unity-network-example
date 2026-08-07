using System;
using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    /// <summary>
    /// Draws one stretched box per parent/child bone pair so a kernel-driven
    /// skeleton reads as a body in the Game view instead of a cloud of joints.
    ///
    /// Runs after <c>KernelSkeletonPoseApplicator</c> (execution order 10000),
    /// which writes the bone local transforms in its own LateUpdate.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(20000)]
    public sealed class NetworkSkeletonLinkView : MonoBehaviour
    {
        private const float MinimumLinkLength = 1e-4f;

        private Transform[] parents = Array.Empty<Transform>();
        private Transform[] children = Array.Empty<Transform>();
        private Transform[] links = Array.Empty<Transform>();
        private float thickness = 0.7f;

        public void Configure(
            Transform linkRoot,
            Transform[] parentBones,
            Transform[] childBones,
            float linkThickness,
            Material material)
        {
            if (linkRoot == null)
            {
                throw new ArgumentNullException(nameof(linkRoot));
            }
            if (parentBones == null)
            {
                throw new ArgumentNullException(nameof(parentBones));
            }
            if (childBones == null)
            {
                throw new ArgumentNullException(nameof(childBones));
            }
            if (parentBones.Length != childBones.Length)
            {
                throw new ArgumentException(
                    "Parent and child bone counts must match.",
                    nameof(childBones));
            }

            parents = parentBones;
            children = childBones;
            thickness = Mathf.Max(0.001f, linkThickness);
            links = new Transform[parentBones.Length];
            for (int index = 0; index < parentBones.Length; ++index)
            {
                GameObject link = GameObject.CreatePrimitive(PrimitiveType.Cube);
                link.name = "Link_" + childBones[index].name;
                NetworkMonsterSimRigFactory.DestroyComponent(link.GetComponent<Collider>());
                if (material != null)
                {
                    link.GetComponent<MeshRenderer>().sharedMaterial = material;
                }
                link.transform.SetParent(linkRoot, false);
                links[index] = link.transform;
            }

            UpdateLinks();
        }

        private void LateUpdate()
        {
            UpdateLinks();
        }

        private void UpdateLinks()
        {
            for (int index = 0; index < links.Length; ++index)
            {
                Transform link = links[index];
                Transform parent = parents[index];
                Transform child = children[index];
                if (link == null || parent == null || child == null)
                {
                    continue;
                }

                Vector3 from = parent.position;
                Vector3 to = child.position;
                Vector3 delta = to - from;
                float length = delta.magnitude;
                if (length < MinimumLinkLength)
                {
                    link.gameObject.SetActive(false);
                    continue;
                }

                if (!link.gameObject.activeSelf)
                {
                    link.gameObject.SetActive(true);
                }
                link.position = from + delta * 0.5f;
                link.rotation = Quaternion.LookRotation(delta / length, Vector3.up);
                link.localScale = new Vector3(thickness, thickness, length);
            }
        }
    }
}
