using System;
using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    [CreateAssetMenu(
        fileName = "NetworkPrefabCatalog",
        menuName = "Network Example/Presentation/Prefab Catalog")]
    public sealed class NetworkPrefabCatalog : ScriptableObject
    {
        [Serializable]
        public struct ActorPrefabBinding
        {
            [InspectorName("Actor Entity Template ID")]
            [Tooltip(
                "For actor render states, RenderEntityState.template_id contains " +
                "the actor template ID.")]
            public uint actorTemplateId;

            public GameObject prefab;

            public ActorPrefabBinding(uint actorTemplateId, GameObject prefab)
            {
                this.actorTemplateId = actorTemplateId;
                this.prefab = prefab;
            }
        }

        [Serializable]
        public struct ProjectilePrefabBinding
        {
            public uint projectileTemplateId;
            public GameObject prefab;

            public ProjectilePrefabBinding(uint projectileTemplateId, GameObject prefab)
            {
                this.projectileTemplateId = projectileTemplateId;
                this.prefab = prefab;
            }
        }

        [Serializable]
        public struct PropPrefabBinding
        {
            [InspectorName("Item Template ID")]
            [Tooltip(
                "For item-backed prop render states, RenderEntityState.template_id " +
                "contains the item template ID. Pure props use the fallback.")]
            public uint itemTemplateId;

            public GameObject prefab;

            public PropPrefabBinding(uint itemTemplateId, GameObject prefab)
            {
                this.itemTemplateId = itemTemplateId;
                this.prefab = prefab;
            }
        }

        [SerializeField]
        private ActorPrefabBinding[] actorPrefabs = Array.Empty<ActorPrefabBinding>();

        [SerializeField]
        private ProjectilePrefabBinding[] projectilePrefabs =
            Array.Empty<ProjectilePrefabBinding>();

        [SerializeField]
        private PropPrefabBinding[] propPrefabs = Array.Empty<PropPrefabBinding>();

        [Header("Fallbacks")]
        [SerializeField]
        private GameObject playerActorFallback;

        [SerializeField]
        private GameObject agentActorFallback;

        [SerializeField]
        private GameObject projectileFallback;

        [SerializeField]
        private GameObject propFallback;

        [SerializeField]
        private GameObject entityFallback;

        public ActorPrefabBinding[] ActorPrefabs => actorPrefabs;
        public ProjectilePrefabBinding[] ProjectilePrefabs => projectilePrefabs;
        public PropPrefabBinding[] PropPrefabs => propPrefabs;
        public GameObject PlayerActorFallback => playerActorFallback;
        public GameObject AgentActorFallback => agentActorFallback;
        public GameObject ProjectileFallback => projectileFallback;
        public GameObject PropFallback => propFallback;
        public GameObject EntityFallback => entityFallback;

        public bool TryGetActorPrefab(uint actorTemplateId, out GameObject prefab)
        {
            if (actorPrefabs != null)
            {
                for (int index = 0; index < actorPrefabs.Length; ++index)
                {
                    ActorPrefabBinding binding = actorPrefabs[index];
                    if (binding.actorTemplateId == actorTemplateId &&
                        binding.prefab != null)
                    {
                        prefab = binding.prefab;
                        return true;
                    }
                }
            }

            prefab = null;
            return false;
        }

        public bool TryGetProjectilePrefab(uint projectileTemplateId, out GameObject prefab)
        {
            if (projectilePrefabs != null)
            {
                for (int index = 0; index < projectilePrefabs.Length; ++index)
                {
                    ProjectilePrefabBinding binding = projectilePrefabs[index];
                    if (binding.projectileTemplateId == projectileTemplateId &&
                        binding.prefab != null)
                    {
                        prefab = binding.prefab;
                        return true;
                    }
                }
            }

            prefab = null;
            return false;
        }

        public bool TryGetPropPrefab(uint itemTemplateId, out GameObject prefab)
        {
            if (propPrefabs != null)
            {
                for (int index = 0; index < propPrefabs.Length; ++index)
                {
                    PropPrefabBinding binding = propPrefabs[index];
                    if (binding.itemTemplateId == itemTemplateId &&
                        binding.prefab != null)
                    {
                        prefab = binding.prefab;
                        return true;
                    }
                }
            }

            prefab = null;
            return false;
        }

        public void Configure(
            ActorPrefabBinding[] actors,
            ProjectilePrefabBinding[] projectiles,
            GameObject playerFallback = null,
            GameObject agentFallback = null,
            GameObject defaultProjectile = null,
            GameObject defaultEntity = null)
        {
            actorPrefabs = actors ?? Array.Empty<ActorPrefabBinding>();
            projectilePrefabs = projectiles ?? Array.Empty<ProjectilePrefabBinding>();
            playerActorFallback = playerFallback;
            agentActorFallback = agentFallback;
            projectileFallback = defaultProjectile;
            entityFallback = defaultEntity;
        }

        public void ConfigureProps(
            PropPrefabBinding[] props,
            GameObject defaultProp = null)
        {
            propPrefabs = props ?? Array.Empty<PropPrefabBinding>();
            propFallback = defaultProp;
        }
    }
}
