using System.Collections.Generic;
using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    [DisallowMultipleComponent]
    public sealed class NetworkPrefabRegistry : MonoBehaviour
    {
        public const string DefaultCatalogResourcePath = "NetworkPrefabCatalog";

        private static readonly Vector3 ProjectileLocalScale = new Vector3(0.1f, 0.1f, 0.1f);
        private const float PlayerCapsuleHalfHeight = 0.55f;
        private const float PlayerCapsuleRadius = 0.35f;
        private const float AgentCapsuleHalfHeight = 0.4f;
        private const float AgentCapsuleRadius = 0.4f;

        [SerializeField]
        [Tooltip(
            "Shared template-id to prefab index. When empty, " +
            "Resources/NetworkPrefabCatalog is loaded automatically.")]
        private NetworkPrefabCatalog catalog;

        private readonly HashSet<string> warnings = new HashSet<string>();
        private bool catalogValidated;

        public NetworkPrefabCatalog Catalog => ResolveCatalog();

        public void Configure(NetworkPrefabCatalog prefabCatalog)
        {
            catalog = prefabCatalog;
            warnings.Clear();
            catalogValidated = false;
        }

        public GameObject InstantiateVisual(KernelEntityType entityType, Transform parent)
        {
            return InstantiateVisual(
                new RenderEntityState { entity_type = entityType, actor_type = KernelActorType.Unknown },
                parent);
        }

        public GameObject InstantiateVisual(RenderEntityState state, Transform parent)
        {
            GameObject prefab = GetPrefab(state);
            bool usesProceduralPlaceholder = prefab == null;
            GameObject visual = prefab == null
                ? CreatePlaceholder(state)
                : Instantiate(prefab);

            visual.name = NameFor(state);
            visual.transform.SetParent(parent, false);
            ApplyVisualDefaults(visual, state, usesProceduralPlaceholder);
            return visual;
        }

        private GameObject GetPrefab(RenderEntityState state)
        {
            NetworkPrefabCatalog resolvedCatalog = ResolveCatalog();
            ValidateCatalogOnce(resolvedCatalog);

            if (state.entity_type == KernelEntityType.Projectile)
            {
                if (resolvedCatalog != null &&
                    resolvedCatalog.TryGetProjectilePrefab(
                        state.projectile_template_id,
                        out GameObject projectilePrefab))
                {
                    return projectilePrefab;
                }

                WarnMissingOnce("projectile", state.projectile_template_id);
                return resolvedCatalog != null
                    ? resolvedCatalog.ProjectileFallback
                    : null;
            }

            if (state.entity_type == KernelEntityType.Actor)
            {
                if (resolvedCatalog != null &&
                    resolvedCatalog.TryGetActorPrefab(
                        state.actor_template_id,
                        out GameObject actorPrefab))
                {
                    return actorPrefab;
                }

                WarnMissingOnce("actor", state.actor_template_id);
                if (resolvedCatalog == null)
                {
                    return null;
                }

                return state.actor_type == KernelActorType.Agent
                    ? resolvedCatalog.AgentActorFallback ??
                        resolvedCatalog.PlayerActorFallback
                    : resolvedCatalog.PlayerActorFallback;
            }

            return resolvedCatalog != null ? resolvedCatalog.EntityFallback : null;
        }

        private NetworkPrefabCatalog ResolveCatalog()
        {
            if (catalog == null)
            {
                catalog = Resources.Load<NetworkPrefabCatalog>(
                    DefaultCatalogResourcePath);
            }

            return catalog;
        }

        private void ValidateCatalogOnce(NetworkPrefabCatalog resolvedCatalog)
        {
            if (catalogValidated || resolvedCatalog == null)
            {
                return;
            }

            catalogValidated = true;
            var actorIds = new HashSet<uint>();
            NetworkPrefabCatalog.ActorPrefabBinding[] actorBindings =
                resolvedCatalog.ActorPrefabs;
            if (actorBindings != null)
            {
                for (int index = 0; index < actorBindings.Length; ++index)
                {
                    NetworkPrefabCatalog.ActorPrefabBinding binding =
                        actorBindings[index];
                    if (!actorIds.Add(binding.actorTemplateId))
                    {
                        WarnOnce(
                            "duplicate-actor-" + binding.actorTemplateId,
                            "Network prefab catalog contains duplicate actor template id " +
                            binding.actorTemplateId +
                            "; the first non-null prefab wins.");
                    }
                    if (binding.prefab == null)
                    {
                        WarnOnce(
                            "null-actor-" + binding.actorTemplateId,
                            "Network prefab catalog actor template id " +
                            binding.actorTemplateId +
                            " has no prefab.");
                    }
                }
            }

            var projectileIds = new HashSet<uint>();
            NetworkPrefabCatalog.ProjectilePrefabBinding[] projectileBindings =
                resolvedCatalog.ProjectilePrefabs;
            if (projectileBindings == null)
            {
                return;
            }

            for (int index = 0; index < projectileBindings.Length; ++index)
            {
                NetworkPrefabCatalog.ProjectilePrefabBinding binding =
                    projectileBindings[index];
                if (!projectileIds.Add(binding.projectileTemplateId))
                {
                    WarnOnce(
                        "duplicate-projectile-" + binding.projectileTemplateId,
                        "Network prefab catalog contains duplicate projectile template id " +
                        binding.projectileTemplateId +
                        "; the first non-null prefab wins.");
                }
                if (binding.prefab == null)
                {
                    WarnOnce(
                        "null-projectile-" + binding.projectileTemplateId,
                        "Network prefab catalog projectile template id " +
                        binding.projectileTemplateId +
                        " has no prefab.");
                }
            }
        }

        private void WarnMissingOnce(string kind, uint templateId)
        {
            WarnOnce(
                "missing-" + kind + "-" + templateId,
                "No exact " +
                kind +
                " prefab is registered for template id " +
                templateId +
                "; using the configured fallback.");
        }

        private void WarnOnce(string key, string message)
        {
            if (warnings.Add(key))
            {
                Debug.LogWarning(message, this);
            }
        }

        private static GameObject CreatePlaceholder(RenderEntityState state)
        {
            PrimitiveType primitiveType = PrimitiveFor(state.entity_type);

            GameObject primitive = GameObject.CreatePrimitive(primitiveType);

            Collider collider = primitive.GetComponent<Collider>();
            if (collider != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(collider);
                }
                else
                {
                    DestroyImmediate(collider);
                }
            }

            Renderer renderer = primitive.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = ColorFor(state);
            }

            if (state.entity_type != KernelEntityType.Actor)
            {
                return primitive;
            }

            // Kernel actor positions are ground contact points. Keep that position on the
            // placeholder root and offset only the capsule mesh so its lower end sits at y=0,
            // matching the character controller's local center and total capsule height.
            GameObject placeholder = new GameObject();
            primitive.name = "CapsuleVisual";
            primitive.transform.SetParent(placeholder.transform, false);

            CapsuleDimensions dimensions = CapsuleDimensionsFor(state.actor_type);
            float centerHeight = dimensions.halfHeight + dimensions.radius;
            primitive.transform.localPosition = Vector3.up * centerHeight;
            primitive.transform.localScale = new Vector3(
                dimensions.radius * 2f,
                centerHeight,
                dimensions.radius * 2f);

            return placeholder;
        }

        private static CapsuleDimensions CapsuleDimensionsFor(KernelActorType actorType)
        {
            return actorType == KernelActorType.Agent
                ? new CapsuleDimensions(AgentCapsuleHalfHeight, AgentCapsuleRadius)
                : new CapsuleDimensions(PlayerCapsuleHalfHeight, PlayerCapsuleRadius);
        }

        private static void ApplyVisualDefaults(
            GameObject visual,
            RenderEntityState state,
            bool usesProceduralPlaceholder)
        {
            if (state.entity_type == KernelEntityType.Projectile &&
                usesProceduralPlaceholder)
            {
                // This applies only to the procedural emergency fallback. Registered
                // projectile prefabs retain the scale authored by the user.
                visual.transform.localScale = ProjectileLocalScale;
            }
        }

        private static PrimitiveType PrimitiveFor(KernelEntityType entityType)
        {
            switch (entityType)
            {
                case KernelEntityType.Projectile:
                    return PrimitiveType.Sphere;
                default:
                    return PrimitiveType.Capsule;
            }
        }

        private static Color ColorFor(RenderEntityState state)
        {
            if (state.entity_type == KernelEntityType.Projectile)
            {
                return new Color(1f, 0.86f, 0.2f);
            }

            if (state.entity_type == KernelEntityType.Actor)
            {
                return state.actor_type == KernelActorType.Agent
                    ? new Color(1f, 0.32f, 0.24f)
                    : new Color(0.18f, 0.55f, 1f);
            }

            return new Color(0.65f, 0.65f, 0.65f);
        }

        private static string NameFor(RenderEntityState state)
        {
            if (state.entity_type == KernelEntityType.Actor && state.actor_type != KernelActorType.Unknown)
            {
                return "NetEntity_" + state.entity_type + "_" + state.actor_type;
            }

            return "NetEntity_" + state.entity_type;
        }

        private readonly struct CapsuleDimensions
        {
            public readonly float halfHeight;
            public readonly float radius;

            public CapsuleDimensions(float halfHeight, float radius)
            {
                this.halfHeight = halfHeight;
                this.radius = radius;
            }
        }
    }
}
