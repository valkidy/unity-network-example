using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    [DisallowMultipleComponent]
    public sealed class NetworkPrefabRegistry : MonoBehaviour
    {
        private static readonly Vector3 ProjectileLocalScale = new Vector3(0.1f, 0.1f, 0.1f);
        private const float PlayerCapsuleHalfHeight = 0.55f;
        private const float PlayerCapsuleRadius = 0.35f;
        private const float AgentCapsuleHalfHeight = 0.4f;
        private const float AgentCapsuleRadius = 0.4f;

        [SerializeField]
        private GameObject playerPrefab = null;

        [SerializeField]
        private GameObject enemyPrefab = null;

        [SerializeField]
        private GameObject projectilePrefab = null;

        [SerializeField]
        private GameObject fallbackPrefab = null;

        public GameObject InstantiateVisual(KernelEntityType entityType, Transform parent)
        {
            return InstantiateVisual(
                new RenderEntityState { entity_type = entityType, actor_type = KernelActorType.Unknown },
                parent);
        }

        public GameObject InstantiateVisual(RenderEntityState state, Transform parent)
        {
            GameObject prefab = GetPrefab(state);
            GameObject visual = prefab == null
                ? CreatePlaceholder(state)
                : Instantiate(prefab);

            visual.name = NameFor(state);
            visual.transform.SetParent(parent, false);
            ApplyVisualDefaults(visual, state);
            return visual;
        }

        private GameObject GetPrefab(RenderEntityState state)
        {
            if (state.entity_type == KernelEntityType.Projectile)
            {
                return projectilePrefab;
            }

            if (state.entity_type == KernelEntityType.Actor)
            {
                if (state.actor_type == KernelActorType.Agent)
                {
                    return enemyPrefab != null ? enemyPrefab : playerPrefab;
                }

                return playerPrefab;
            }

            return fallbackPrefab;
        }

        private static GameObject CreatePlaceholder(RenderEntityState state)
        {
            PrimitiveType primitiveType = PrimitiveFor(state.entity_type);

            GameObject primitive = GameObject.CreatePrimitive(primitiveType);

            Collider collider = primitive.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
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

        private static void ApplyVisualDefaults(GameObject visual, RenderEntityState state)
        {
            if (state.entity_type == KernelEntityType.Projectile)
            {
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
