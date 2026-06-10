using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    [DisallowMultipleComponent]
    public sealed class NetworkPrefabRegistry : MonoBehaviour
    {
        private static readonly Vector3 ProjectileLocalScale = new Vector3(0.1f, 0.1f, 0.1f);
        private static readonly Vector3 AreaEffectLocalScale = new Vector3(2.0f, 0.05f, 2.0f);
        private static readonly Vector3 BeamLocalScale = new Vector3(0.2f, 0.2f, 3.0f);

        [SerializeField]
        private GameObject playerPrefab = null;

        [SerializeField]
        private GameObject enemyPrefab = null;

        [SerializeField]
        private GameObject projectilePrefab = null;

        [SerializeField]
        private GameObject areaEffectPrefab = null;

        [SerializeField]
        private GameObject beamPrefab = null;

        [SerializeField]
        private GameObject fallbackPrefab = null;

        public GameObject InstantiateVisual(KernelEntityType entityType, Transform parent)
        {
            GameObject prefab = GetPrefab(entityType);
            GameObject visual = prefab == null
                ? CreatePlaceholder(entityType)
                : Instantiate(prefab);

            visual.name = "NetEntity_" + entityType;
            visual.transform.SetParent(parent, false);
            ApplyVisualDefaults(visual, entityType);
            return visual;
        }

        private GameObject GetPrefab(KernelEntityType entityType)
        {
            switch (entityType)
            {
                case KernelEntityType.Player:
                    return playerPrefab;
                case KernelEntityType.Enemy:
                    return enemyPrefab != null ? enemyPrefab : playerPrefab;
                case KernelEntityType.Projectile:
                    return projectilePrefab;
                case KernelEntityType.AreaEffect:
                    return areaEffectPrefab;
                case KernelEntityType.Beam:
                    return beamPrefab;
                default:
                    return fallbackPrefab;
            }
        }

        private static GameObject CreatePlaceholder(KernelEntityType entityType)
        {
            PrimitiveType primitiveType = PrimitiveFor(entityType);

            GameObject placeholder = GameObject.CreatePrimitive(primitiveType);

            Collider collider = placeholder.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }

            Renderer renderer = placeholder.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = ColorFor(entityType);
            }

            return placeholder;
        }

        private static void ApplyVisualDefaults(GameObject visual, KernelEntityType entityType)
        {
            if (entityType == KernelEntityType.Projectile)
            {
                visual.transform.localScale = ProjectileLocalScale;
            }
            else if (entityType == KernelEntityType.AreaEffect)
            {
                visual.transform.localScale = AreaEffectLocalScale;
            }
            else if (entityType == KernelEntityType.Beam)
            {
                visual.transform.localScale = BeamLocalScale;
            }
        }

        private static PrimitiveType PrimitiveFor(KernelEntityType entityType)
        {
            switch (entityType)
            {
                case KernelEntityType.Projectile:
                    return PrimitiveType.Sphere;
                case KernelEntityType.AreaEffect:
                    return PrimitiveType.Cylinder;
                case KernelEntityType.Beam:
                    return PrimitiveType.Cube;
                default:
                    return PrimitiveType.Capsule;
            }
        }

        private static Color ColorFor(KernelEntityType entityType)
        {
            switch (entityType)
            {
                case KernelEntityType.Player:
                    return new Color(0.18f, 0.55f, 1f);
                case KernelEntityType.Enemy:
                    return new Color(1f, 0.32f, 0.24f);
                case KernelEntityType.Projectile:
                    return new Color(1f, 0.86f, 0.2f);
                case KernelEntityType.AreaEffect:
                    return new Color(1f, 0.38f, 0.08f);
                case KernelEntityType.Beam:
                    return new Color(0.35f, 0.95f, 1f);
                default:
                    return new Color(0.65f, 0.65f, 0.65f);
            }
        }
    }
}
