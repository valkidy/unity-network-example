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
                "The current render ABI exposes an actor entity-template selection through " +
                "RenderEntityState.actor_template_id.")]
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

        [SerializeField]
        private ActorPrefabBinding[] actorPrefabs = Array.Empty<ActorPrefabBinding>();

        [SerializeField]
        private ProjectilePrefabBinding[] projectilePrefabs =
            Array.Empty<ProjectilePrefabBinding>();

        [Header("Fallbacks")]
        [SerializeField]
        private GameObject playerActorFallback;

        [SerializeField]
        private GameObject agentActorFallback;

        [SerializeField]
        private GameObject projectileFallback;

        [SerializeField]
        private GameObject entityFallback;

        public ActorPrefabBinding[] ActorPrefabs => actorPrefabs;
        public ProjectilePrefabBinding[] ProjectilePrefabs => projectilePrefabs;
        public GameObject PlayerActorFallback => playerActorFallback;
        public GameObject AgentActorFallback => agentActorFallback;
        public GameObject ProjectileFallback => projectileFallback;
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
    }
}
