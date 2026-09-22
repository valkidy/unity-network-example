using System;
using System.Collections.Generic;
using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.Common
{
    /// <summary>
    /// Every collider shape of one frame: the kernel's live query, completed
    /// with shapes rebuilt from its catalog for render entities the query does
    /// not cover.
    /// </summary>
    /// <remarks>
    /// Against a dedicated server (2026-09-22) a client's live query returned a
    /// hit collider for every actor (Aabb) and prop (OrientedBox) it rendered,
    /// and nothing needed rebuilding. The rebuild is the fallback for any entity
    /// the query does not cover: the collider template is resolved from the
    /// kernel's parsed catalog -- the per-instance collider_template_id, else the
    /// entity-type binding, else a projectile's template -- and placed at the
    /// render transform plus the binding's local offset. The catalog is static
    /// after load and is read once, until <see cref="InvalidateCatalog"/>.
    /// </remarks>
    public sealed class KernelColliderShapeSource
    {
        private readonly string owner;
        private KernelColliderShapeView[] shapes;
        private readonly Dictionary<uint, KernelColliderTemplateDefinition> colliderTemplates =
            new Dictionary<uint, KernelColliderTemplateDefinition>();
        private readonly Dictionary<ushort, KernelColliderBindingDefinition> colliderBindings =
            new Dictionary<ushort, KernelColliderBindingDefinition>();
        // Projectiles have no entity-type binding: their collider is on the projectile template.
        private readonly Dictionary<uint, KernelProjectileTemplateDefinition> projectileTemplates =
            new Dictionary<uint, KernelProjectileTemplateDefinition>();
        private readonly HashSet<uint> liveColliderNetIds = new HashSet<uint>();
        private readonly HashSet<uint> liveVisionNetIds = new HashSet<uint>();

        /// <param name="owner">Names the owner in the buffer growth warning.</param>
        public KernelColliderShapeSource(string owner, int initialCapacity = 512)
        {
            this.owner = owner;
            shapes = new KernelColliderShapeView[Mathf.Max(1, initialCapacity)];
        }

        /// <summary>This frame's shapes; the first <see cref="Count"/> are valid.</summary>
        public KernelColliderShapeView[] Shapes => shapes;
        public int Count { get; private set; }
        /// <summary>How many of <see cref="Count"/> came from the live query; the rest were rebuilt.</summary>
        public int LiveCount { get; private set; }
        public bool CatalogLoaded { get; private set; }
        public int ColliderTemplateCount => colliderTemplates.Count;
        public int ColliderBindingCount => colliderBindings.Count;
        public int ProjectileTemplateCount => projectileTemplates.Count;

        /// <summary>The live query returned a vision-purpose shape for this entity.</summary>
        public bool HasLiveVisionShape(uint netId) => liveVisionNetIds.Contains(netId);
        public bool TryGetColliderTemplate(uint templateId, out KernelColliderTemplateDefinition template) =>
            colliderTemplates.TryGetValue(templateId, out template);
        public bool TryGetProjectileTemplate(uint templateId, out KernelProjectileTemplateDefinition template) =>
            projectileTemplates.TryGetValue(templateId, out template);

        public void Clear()
        {
            Count = LiveCount = 0;
            liveColliderNetIds.Clear();
            liveVisionNetIds.Clear();
        }

        public void InvalidateCatalog()
        {
            CatalogLoaded = false;
            colliderTemplates.Clear();
            colliderBindings.Clear();
            projectileTemplates.Clear();
        }

        public void Capture(NetworkExample.Kernel.Kernel kernel, RenderEntityState[] states, int count)
        {
            Clear();
            if (kernel == null) return;
            EnsureCatalog(kernel);

            // One query-all call returns every collider the kernel has materialized. It
            // reports the full count even when the buffer is short, so grow once and
            // re-query instead of silently truncating.
            uint found = kernel.QueryColliderShapes(null, shapes);
            if (found > (uint)shapes.Length)
            {
                Grow((int)found);
                found = kernel.QueryColliderShapes(null, shapes);
            }
            Count = LiveCount = found > (uint)shapes.Length ? shapes.Length : (int)found;
            for (int index = 0; index < Count; ++index)
            {
                liveColliderNetIds.Add(shapes[index].entity_net_id);
                if ((shapes[index].purpose_flags & (uint)KernelColliderPurpose.Vision) != 0)
                    liveVisionNetIds.Add(shapes[index].entity_net_id);
            }
            AppendRebuilt(states, count);
        }

        /// <summary>
        /// Replaces the catalog the shapes are rebuilt from, for a caller that
        /// has it without a kernel.
        /// </summary>
        public void SetCatalog(KernelColliderTemplateDefinition[] templates,
            KernelColliderBindingDefinition[] bindings, KernelProjectileTemplateDefinition[] projectiles)
        {
            InvalidateCatalog();
            if (templates != null)
                foreach (KernelColliderTemplateDefinition template in templates)
                    colliderTemplates[template.template_id] = template;
            if (bindings != null)
                foreach (KernelColliderBindingDefinition binding in bindings)
                    colliderBindings[binding.entity_type] = binding;
            if (projectiles != null)
                foreach (KernelProjectileTemplateDefinition projectile in projectiles)
                    projectileTemplates[projectile.projectile_template_id] = projectile;
            CatalogLoaded = true;
        }

        /// <summary>Captures only the shapes rebuilt from the catalog, with no live query.</summary>
        public void CaptureFromCatalog(RenderEntityState[] states, int count)
        {
            Clear();
            AppendRebuilt(states, count);
        }

        private void AppendRebuilt(RenderEntityState[] states, int count)
        {
            if (states == null) return;
            count = Mathf.Clamp(count, 0, states.Length);
            for (int index = 0; index < count; ++index)
            {
                RenderEntityState state = states[index];
                if (state.net_id != 0 && liveColliderNetIds.Contains(state.net_id)) continue;
                if (!TryRebuild(state, out KernelColliderShapeView shape)) continue;
                if (Count == shapes.Length) Grow(Count + 1);
                shapes[Count++] = shape;
            }
        }

        private void EnsureCatalog(NetworkExample.Kernel.Kernel kernel)
        {
            if (CatalogLoaded) return;
            uint templateCount = kernel.GetColliderTemplates(null);
            // Not loaded yet (or the kernel is not ready): retry next frame.
            if (templateCount == 0) return;

            var templates = new KernelColliderTemplateDefinition[templateCount];
            Array.Resize(ref templates, (int)Math.Min(kernel.GetColliderTemplates(templates), templateCount));
            KernelColliderBindingDefinition[] bindings = null;
            uint bindingCount = kernel.GetColliderBindings(null);
            if (bindingCount > 0)
            {
                bindings = new KernelColliderBindingDefinition[bindingCount];
                Array.Resize(ref bindings, (int)Math.Min(kernel.GetColliderBindings(bindings), bindingCount));
            }
            KernelProjectileTemplateDefinition[] projectiles = null;
            uint projectileCount = kernel.GetProjectileTemplates(null);
            if (projectileCount > 0)
            {
                projectiles = new KernelProjectileTemplateDefinition[projectileCount];
                Array.Resize(ref projectiles, (int)Math.Min(kernel.GetProjectileTemplates(projectiles), projectileCount));
            }
            SetCatalog(templates, bindings, projectiles);
        }

        private bool TryRebuild(RenderEntityState state, out KernelColliderShapeView shape)
        {
            shape = default;
            // The per-instance collider id wins; fall back to the entity-type binding.
            uint templateId = state.collider_template_id;
            bool hasBinding = colliderBindings.TryGetValue((ushort)state.entity_type,
                out KernelColliderBindingDefinition binding);
            if (templateId == 0 && hasBinding) templateId = binding.collider_template_id;
            if (templateId == 0 && state.entity_type == KernelEntityType.Projectile &&
                projectileTemplates.TryGetValue(state.template_id, out KernelProjectileTemplateDefinition projectile))
                templateId = projectile.mechanics.collider_template_id;
            if (templateId == 0 ||
                !colliderTemplates.TryGetValue(templateId, out KernelColliderTemplateDefinition template))
                return false;

            Vector3 entityPosition = ToVector3(state.position);
            Quaternion entityRotation = ToQuaternion(state.rotation);
            Vector3 localOffset = ToVector3(template.center);
            Quaternion localRotation = Quaternion.identity;
            if (hasBinding)
            {
                localOffset += ToVector3(binding.local_position);
                localRotation = ToQuaternion(binding.local_rotation);
            }
            Quaternion worldRotation = entityRotation * localRotation;
            Vector3 worldCenter = entityPosition + entityRotation * localOffset;

            shape.entity_net_id = state.net_id;
            shape.entity_type = (ushort)state.entity_type;
            shape.actor_type = state.actor_type;
            shape.collider_template_id = templateId;
            shape.shape_type = template.shape_type;
            shape.shape_params = template.shape_params;
            shape.purpose_flags = template.purpose_flags;
            shape.world_center = new KernelVec3(worldCenter.x, worldCenter.y, worldCenter.z);
            shape.world_rotation = new KernelQuat(worldRotation.x, worldRotation.y, worldRotation.z, worldRotation.w);
            // Bound colliders are never segments (those are transient hit-scan colliders
            // from the live query), so the endpoints stay at the origin; a Segment
            // template would collapse to a point.
            return true;
        }

        // Doubling keeps repeated growth amortized.
        private void Grow(int required)
        {
            int oldCapacity = shapes.Length;
            if (required <= oldCapacity) return;
            int newCapacity = Mathf.Max(required, oldCapacity * 2);
            Array.Resize(ref shapes, newCapacity);
            Debug.LogWarning($"[{owner}] Collider shape buffer grew from {oldCapacity} to {newCapacity} " +
                $"(needed {required}) to avoid truncating collider shapes.");
        }

        private static Vector3 ToVector3(KernelVec3 value) => new Vector3(value.x, value.y, value.z);
        private static Vector3 ToVector3(KernelVec4 value) => new Vector3(value.x, value.y, value.z);
        private static Quaternion ToQuaternion(KernelQuat value) =>
            value.x == 0f && value.y == 0f && value.z == 0f && value.w == 0f
                ? Quaternion.identity : new Quaternion(value.x, value.y, value.z, value.w);
    }
}
