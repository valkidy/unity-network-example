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
        private readonly Dictionary<ulong, KnownEntity> knownEntities =
            new Dictionary<ulong, KnownEntity>();
        private readonly List<ulong> entityKeysToRemove = new List<ulong>();
        private readonly HashSet<RemoteCommitKey> remoteCommitDedup =
            new HashSet<RemoteCommitKey>();
        private readonly Queue<RemoteCommitKey> remoteCommitOrder =
            new Queue<RemoteCommitKey>();
        private readonly HashSet<LandedEventKey> landedEventDedup =
            new HashSet<LandedEventKey>();
        private readonly Queue<LandedEventKey> landedEventOrder =
            new Queue<LandedEventKey>();

        private const int MaxRememberedRemoteCommits = 512;
        private const int MaxRememberedLandedEvents = 512;

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
                }
                entityRegistry.RegisterNetId(state.net_id, visual);
                bool wasServerBacked =
                    knownEntities.TryGetValue(entityKey, out KnownEntity known) &&
                    known.serverBacked;
                knownEntities[entityKey] = new KnownEntity(
                    wasServerBacked || state.net_id != 0);

                if (state.entity_type == KernelEntityType.Projectile)
                {
                    ApplyProjectileState(visual, state);
                }
                else if (state.entity_type == KernelEntityType.Actor &&
                    state.status == RenderEntityStatus.Stale)
                {
                    GetOrAddActorView(visual).SetStale(true);
                }
                else
                {
                    ApplyTransform(visual.transform, state);
                    ApplyActorState(visual, state);
                }
            }

            entityKeysToRemove.Clear();
            foreach (KeyValuePair<ulong, KnownEntity> pair in knownEntities)
            {
                if (visibleThisFrame.Contains(pair.Key) || pair.Value.serverBacked)
                {
                    continue;
                }

                entityKeysToRemove.Add(pair.Key);
            }

            for (int index = 0; index < entityKeysToRemove.Count; ++index)
            {
                ulong entityKey = entityKeysToRemove[index];
                entityRegistry.Remove(entityKey);
                knownEntities.Remove(entityKey);
            }
            entityKeysToRemove.Clear();
        }

        public void Clear()
        {
            knownEntities.Clear();
            visibleThisFrame.Clear();
            remoteCommitDedup.Clear();
            remoteCommitOrder.Clear();
            landedEventDedup.Clear();
            landedEventOrder.Clear();
            entityKeysToRemove.Clear();
            entityRegistry?.Clear();
        }

        public void BeginPredictedLocalAction(uint localPlayerNetId, ActionIntent intent)
        {
            if (intent.action_instance_id == 0 ||
                entityRegistry == null ||
                !entityRegistry.TryGetByNetId(localPlayerNetId, out GameObject visual))
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
                !entityRegistry.TryGetByNetId(localPlayerNetId, out GameObject visual))
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
                if (!entityRegistry.TryGetByNetId(
                        remoteEvent.actor_net_id,
                        out GameObject visual))
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

        public void ApplyKernelEvents(KernelEvent[] events, int count)
        {
            if (events == null || entityRegistry == null)
            {
                return;
            }

            int safeCount = Mathf.Clamp(count, 0, events.Length);
            for (int index = 0; index < safeCount; ++index)
            {
                KernelEvent kernelEvent = events[index];
                if (kernelEvent.type != KernelEventType.ActorLanded ||
                    kernelEvent.net_id == 0 ||
                    !RememberLandedEvent(
                        new LandedEventKey(kernelEvent.net_id, kernelEvent.tick)) ||
                    !entityRegistry.TryGetByNetId(
                        kernelEvent.net_id,
                        out GameObject visual))
                {
                    continue;
                }

                GetOrAddActorView(visual).PlayActorLanded();
            }
        }

        public void ApplyEntityLifecycleEvents(
            KernelEntityLifecycleEvent[] events,
            int count)
        {
            if (events == null || entityRegistry == null)
            {
                return;
            }

            int safeCount = Mathf.Clamp(count, 0, events.Length);
            for (int index = 0; index < safeCount; ++index)
            {
                KernelEntityLifecycleEvent lifecycleEvent = events[index];
                if (lifecycleEvent.net_id == 0)
                {
                    continue;
                }

                if (entityRegistry.RemoveByNetId(
                        lifecycleEvent.net_id,
                        out ulong entityKey))
                {
                    knownEntities.Remove(entityKey);
                    visibleThisFrame.Remove(entityKey);
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

        private bool RememberLandedEvent(LandedEventKey key)
        {
            if (!landedEventDedup.Add(key))
            {
                return false;
            }

            landedEventOrder.Enqueue(key);
            while (landedEventOrder.Count > MaxRememberedLandedEvents)
            {
                landedEventDedup.Remove(landedEventOrder.Dequeue());
            }
            return true;
        }

        private readonly struct KnownEntity
        {
            public readonly bool serverBacked;

            public KnownEntity(bool serverBacked)
            {
                this.serverBacked = serverBacked;
            }
        }

        private readonly struct LandedEventKey : System.IEquatable<LandedEventKey>
        {
            private readonly uint actorNetId;
            private readonly uint tick;

            public LandedEventKey(uint actorNetId, uint tick)
            {
                this.actorNetId = actorNetId;
                this.tick = tick;
            }

            public bool Equals(LandedEventKey other)
            {
                return actorNetId == other.actorNetId && tick == other.tick;
            }

            public override bool Equals(object obj)
            {
                return obj is LandedEventKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((int)actorNetId * 397) ^ (int)tick;
                }
            }
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
