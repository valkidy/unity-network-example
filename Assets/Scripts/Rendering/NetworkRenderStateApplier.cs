using System.Collections.Generic;
using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    [DisallowMultipleComponent]
    public sealed class NetworkRenderStateApplier : MonoBehaviour
    {
        private const ulong PredictedProjectileKeyMask = 1UL << 63;

        [SerializeField]
        private NetworkEntityRegistry entityRegistry;

        [SerializeField]
        private NetworkPrefabRegistry prefabRegistry;

        [SerializeField]
        private Transform entityRoot;

        private readonly HashSet<ulong> visibleThisFrame = new HashSet<ulong>();
        private readonly List<ulong> knownEntities = new List<ulong>();

        public void Configure(
            NetworkEntityRegistry registry,
            NetworkPrefabRegistry prefabs,
            Transform root)
        {
            entityRegistry = registry;
            prefabRegistry = prefabs;
            entityRoot = root;
        }

        public void Apply(RenderEntityState[] states, int count)
        {
            if (states == null || entityRegistry == null || prefabRegistry == null)
            {
                return;
            }

            visibleThisFrame.Clear();
            int safeCount = Mathf.Clamp(count, 0, states.Length);
            for (int index = 0; index < safeCount; ++index)
            {
                RenderEntityState state = states[index];
                ulong entityKey = EntityKeyFor(state);
                if (entityKey == 0 || !ShouldRender(state))
                {
                    continue;
                }

                visibleThisFrame.Add(entityKey);
                if (!entityRegistry.TryGet(entityKey, out GameObject visual))
                {
                    visual = prefabRegistry.InstantiateVisual(state, entityRoot);
                    entityRegistry.Register(entityKey, visual);
                    knownEntities.Add(entityKey);
                }

                if (state.entity_type == KernelEntityType.Projectile)
                {
                    ApplyProjectileState(visual, state);
                }
                else
                {
                    ApplyTransform(visual.transform, state);
                }
            }

            for (int index = knownEntities.Count - 1; index >= 0; --index)
            {
                ulong entityKey = knownEntities[index];
                if (visibleThisFrame.Contains(entityKey))
                {
                    continue;
                }

                entityRegistry.Remove(entityKey);
                knownEntities.RemoveAt(index);
            }
        }

        public void Clear()
        {
            knownEntities.Clear();
            visibleThisFrame.Clear();
            entityRegistry?.Clear();
        }

        private static bool ShouldRender(RenderEntityState state)
        {
            if (state.entity_type == KernelEntityType.Projectile)
            {
                return state.entity_id != 0 ||
                    state.net_id != 0 ||
                    state.client_action_id != 0;
            }

            return state.net_id != 0;
        }

        private static ulong EntityKeyFor(RenderEntityState state)
        {
            if (state.entity_id != 0)
            {
                return state.entity_id;
            }

            if (state.entity_type == KernelEntityType.Projectile &&
                state.status == RenderEntityStatus.Predicted &&
                state.client_action_id != 0)
            {
                return PredictedProjectileKeyMask | state.client_action_id;
            }

            return state.net_id;
        }

        private static void ApplyProjectileState(GameObject visual, RenderEntityState state)
        {
            NetworkProjectileView view = visual.GetComponent<NetworkProjectileView>();
            if (view == null)
            {
                view = visual.AddComponent<NetworkProjectileView>();
            }

            view.ApplyKernelState(state);
        }

        private static void ApplyTransform(Transform target, RenderEntityState state)
        {
            target.SetPositionAndRotation(
                new Vector3(state.position.x, state.position.y, state.position.z),
                new Quaternion(
                    state.rotation.x,
                    state.rotation.y,
                    state.rotation.z,
                    state.rotation.w));
        }
    }
}
