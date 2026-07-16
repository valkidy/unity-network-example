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
        private readonly HashSet<RemoteCommitKey> remoteCommitDedup =
            new HashSet<RemoteCommitKey>();
        private readonly Queue<RemoteCommitKey> remoteCommitOrder =
            new Queue<RemoteCommitKey>();

        private const int MaxRememberedRemoteCommits = 512;

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
                    ApplyActorState(visual, state);
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
            remoteCommitDedup.Clear();
            remoteCommitOrder.Clear();
            entityRegistry?.Clear();
        }

        public void BeginPredictedLocalAction(uint localPlayerNetId, ActionIntent intent)
        {
            if (intent.action_instance_id == 0 ||
                entityRegistry == null ||
                !entityRegistry.TryGet(localPlayerNetId, out GameObject visual))
            {
                return;
            }

            GetOrAddActorView(visual).BeginPredictedAction(intent);
        }

        public void ApplyLocalActionResults(
            uint localPlayerNetId,
            KernelLocalActionResult[] results,
            int count)
        {
            if (results == null ||
                entityRegistry == null ||
                !entityRegistry.TryGet(localPlayerNetId, out GameObject visual))
            {
                return;
            }

            NetworkActorView view = GetOrAddActorView(visual);
            int safeCount = Mathf.Clamp(count, 0, results.Length);
            for (int index = 0; index < safeCount; ++index)
            {
                view.ApplyLocalActionResult(results[index]);
            }
        }

        public void ApplyRemoteActionPresentationEvents(
            KernelRemoteActionPresentationEvent[] events,
            int count)
        {
            if (events == null || entityRegistry == null)
            {
                return;
            }

            int safeCount = Mathf.Clamp(count, 0, events.Length);
            for (int eventIndex = 0; eventIndex < safeCount; ++eventIndex)
            {
                KernelRemoteActionPresentationEvent remoteEvent = events[eventIndex];
                if (!entityRegistry.TryGet(remoteEvent.actor_net_id, out GameObject visual))
                {
                    continue;
                }

                NetworkActorView view = GetOrAddActorView(visual);
                uint endCommit = (uint)remoteEvent.first_commit_index + remoteEvent.commit_count;
                for (uint commitIndex = remoteEvent.first_commit_index;
                    commitIndex < endCommit;
                    ++commitIndex)
                {
                    var key = new RemoteCommitKey(
                        remoteEvent.actor_net_id,
                        remoteEvent.action_instance_id,
                        commitIndex,
                        remoteEvent.event_type);
                    if (!RememberRemoteCommit(key))
                    {
                        continue;
                    }

                    view.PlayRemoteCommit(remoteEvent, commitIndex);
                }
            }
        }

        private static bool ShouldRender(RenderEntityState state)
        {
            if (state.entity_type == KernelEntityType.Projectile)
            {
                return state.entity_id != 0 ||
                    state.net_id != 0 ||
                    state.action_instance_id != 0;
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
                state.action_instance_id != 0)
            {
                return PredictedProjectileKeyMask | state.action_instance_id;
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

        private static void ApplyActorState(GameObject visual, RenderEntityState state)
        {
            if (state.entity_type != KernelEntityType.Actor)
            {
                return;
            }

            GetOrAddActorView(visual).ApplyContinuousState(state);
        }

        private static NetworkActorView GetOrAddActorView(GameObject visual)
        {
            NetworkActorView view = visual.GetComponent<NetworkActorView>();
            return view != null ? view : visual.AddComponent<NetworkActorView>();
        }

        private bool RememberRemoteCommit(RemoteCommitKey key)
        {
            if (!remoteCommitDedup.Add(key))
            {
                return false;
            }

            remoteCommitOrder.Enqueue(key);
            while (remoteCommitOrder.Count > MaxRememberedRemoteCommits)
            {
                remoteCommitDedup.Remove(remoteCommitOrder.Dequeue());
            }
            return true;
        }

        private readonly struct RemoteCommitKey : System.IEquatable<RemoteCommitKey>
        {
            private readonly uint actorNetId;
            private readonly uint actionInstanceId;
            private readonly uint commitIndex;
            private readonly KernelRemoteActionPresentationEventType eventType;

            public RemoteCommitKey(
                uint actorNetId,
                uint actionInstanceId,
                uint commitIndex,
                KernelRemoteActionPresentationEventType eventType)
            {
                this.actorNetId = actorNetId;
                this.actionInstanceId = actionInstanceId;
                this.commitIndex = commitIndex;
                this.eventType = eventType;
            }

            public bool Equals(RemoteCommitKey other)
            {
                return actorNetId == other.actorNetId &&
                    actionInstanceId == other.actionInstanceId &&
                    commitIndex == other.commitIndex &&
                    eventType == other.eventType;
            }

            public override bool Equals(object obj)
            {
                return obj is RemoteCommitKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = (int)actorNetId;
                    hash = (hash * 397) ^ (int)actionInstanceId;
                    hash = (hash * 397) ^ (int)commitIndex;
                    return (hash * 397) ^ (int)eventType;
                }
            }
        }
    }
}
