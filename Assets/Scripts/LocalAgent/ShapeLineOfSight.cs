using System;
using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.LocalAgent
{
    public interface ILineOfSight
    {
        /// <summary>
        /// Whether nothing blocks the line from eye to target. The colliders of
        /// ignoreNetIdA and ignoreNetIdB (the observer and the target) never block it.
        /// </summary>
        bool IsClear(Vector3 eye, Vector3 target, uint ignoreNetIdA, uint ignoreNetIdB);
    }

    /// <summary>
    /// Line of sight against the kernel's collider shapes, plus an optional
    /// terrain test. Only Hit colliders are solid; projectiles in flight are
    /// ignored, as they are too brief to hide anything behind.
    /// </summary>
    /// <remarks>
    /// Unity's only collider in the scene is the terrain, so props and actors
    /// can occlude only through these shapes. The shapes array is the caller's
    /// buffer and is read, not copied: refresh it with SetShapes once per
    /// observation, not per ray.
    /// </remarks>
    public sealed class ShapeLineOfSight : ILineOfSight
    {
        private KernelColliderShapeView[] shapes = Array.Empty<KernelColliderShapeView>();
        private int count;

        /// <summary>True when the terrain blocks the segment from the first point to the second.</summary>
        public Func<Vector3, Vector3, bool> TerrainBlocks { get; set; }
        public int ShapeCount => count;

        public void SetShapes(KernelColliderShapeView[] shapeBuffer, int shapeCount)
        {
            shapes = shapeBuffer ?? Array.Empty<KernelColliderShapeView>();
            count = Mathf.Clamp(shapeCount, 0, shapes.Length);
        }

        public bool IsClear(Vector3 eye, Vector3 target, uint ignoreNetIdA, uint ignoreNetIdB)
        {
            for (int i = 0; i < count; i++)
            {
                ref readonly KernelColliderShapeView shape = ref shapes[i];
                if ((shape.purpose_flags & (uint)KernelColliderPurpose.Hit) == 0 ||
                    shape.entity_type == (ushort)KernelEntityType.Projectile) continue;
                if (shape.entity_net_id != 0 &&
                    (shape.entity_net_id == ignoreNetIdA || shape.entity_net_id == ignoreNetIdB)) continue;
                if (LineOfSightGeometry.SegmentHitsShape(eye, target, shape)) return false;
            }
            // Last: the terrain test is a physics query, the shapes are arithmetic.
            return TerrainBlocks == null || !TerrainBlocks(eye, target);
        }
    }
}
