using System.Collections.Generic;
using NetworkExample.Kernel;
using NetworkExample.Kernel.Presentation;
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

        [SerializeField]
        [Tooltip(
            "Draws the shots of weapons that spawn nothing to draw. Optional: " +
            "without it hitscan and shotgun fire is invisible, and every other " +
            "weapon is unaffected.")]
        private NetworkHitscanTracers hitscanTracers;

        [SerializeField]
        [Tooltip(
            "Throws splatters where an actor dies. Optional: without it a death " +
            "still plays its animation and leaves nothing on the ground.")]
        private NetworkHitSplatters hitSplatters;

        [SerializeField]
        [Tooltip(
            "Logs every signal that could mean an actor died -- the replicated " +
            "dead flag turning on, the death presentation event, and the " +
            "despawn that follows -- so a death that leaves no mark on the " +
            "ground can be traced to whichever signal did not arrive.")]
        private bool logDeathSignals;

        private readonly HashSet<ulong> visibleThisFrame = new HashSet<ulong>();
        private readonly Dictionary<ulong, KnownEntity> knownEntities =
            new Dictionary<ulong, KnownEntity>();
        private readonly List<ulong> entityKeysToRemove = new List<ulong>();
        // Cached per entity so the rig is not searched for its applicator every
        // frame, and so a rejected pose is reported once instead of per frame.
        private readonly Dictionary<uint, KernelSkeletonPoseApplicator> skeletonApplicators =
            new Dictionary<uint, KernelSkeletonPoseApplicator>();
        private readonly HashSet<uint> skeletonApplyErrors = new HashSet<uint>();
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

        public void ConfigureTracers(NetworkHitscanTracers tracers)
        {
            hitscanTracers = tracers;
        }

        public void ConfigureSplatters(NetworkHitSplatters splatters)
        {
            hitSplatters = splatters;
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
                bool knownBefore =
                    knownEntities.TryGetValue(entityKey, out KnownEntity known);
                bool wasServerBacked = knownBefore && known.serverBacked;
                bool isDead = known.wasDead;

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

                    if (state.entity_type == KernelEntityType.Actor)
                    {
                        isDead = (state.visual_flags &
                            KernelConstants.VisualFlagDead) != 0;
                        // An actor this client watched alive and now sees dead
                        // died in front of it, and the transition happens on
                        // exactly one frame. One that is already dead the first
                        // time it is seen died before this client was watching,
                        // or before it came into range -- marking the ground for
                        // that would drop a fresh splat under every corpse a
                        // late joiner walks up to.
                        if (isDead && !known.wasDead && knownBefore)
                        {
                            LogDeathSignal("dead flag raised", state.net_id);
                            TrySplat(visual);
                        }
                        else if (isDead && !knownBefore)
                        {
                            LogDeathSignal("first seen already dead", state.net_id);
                        }
                    }
                }

                knownEntities[entityKey] = new KnownEntity(
                    wasServerBacked || state.net_id != 0,
                    isDead);
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

        /// <summary>
        /// Drives the bone poses of entities that carry a skeleton. Without this
        /// a replicated rig keeps whatever pose its prefab was instantiated
        /// with -- feet neither moving nor touching the ground -- no matter what
        /// the kernel solves, and nothing reports it: the render state still
        /// says PROCEDURAL because that flag describes where the pose came from,
        /// not whether anything consumed it.
        ///
        /// <paramref name="buffer"/> MUST have been filled at the same render
        /// time as the root states passed to <see cref="Apply"/>. Sampling the
        /// two at different instants puts the pose of one moment onto the root
        /// of another, and every planted foot slides by the difference.
        /// </summary>
        public void ApplySkeletonPoses(
            NetworkExample.Kernel.Kernel kernel,
            SkeletonRenderStateBuffer buffer)
        {
            if (buffer == null || buffer.States == null || entityRegistry == null)
            {
                return;
            }

            int count = buffer.StateCount > (uint)buffer.States.Length
                ? buffer.States.Length
                : (int)buffer.StateCount;
            for (int index = 0; index < count; ++index)
            {
                KernelSkeletonRenderState state = buffer.States[index];
                if (!entityRegistry.TryGetByNetId(state.entity_net_id, out GameObject visual) ||
                    visual == null)
                {
                    continue;
                }

                if (!skeletonApplicators.TryGetValue(
                        state.entity_net_id,
                        out KernelSkeletonPoseApplicator applicator) ||
                    applicator == null)
                {
                    applicator = visual.GetComponentInChildren<KernelSkeletonPoseApplicator>(true);
                    if (applicator == null)
                    {
                        continue;
                    }

                    skeletonApplicators[state.entity_net_id] = applicator;
                }

                if (!applicator.TryApply(kernel, state, buffer, out string error) &&
                    skeletonApplyErrors.Add(state.entity_net_id))
                {
                    // Once per entity: a rejected pose leaves the rig at its
                    // last applied transforms, which looks like a locomotion
                    // fault rather than a rejection.
                    Debug.LogWarning(
                        "Skeleton pose rejected for net_id=" + state.entity_net_id +
                        ": " + error);
                }
            }
        }

        public void Clear()
        {
            skeletonApplicators.Clear();
            skeletonApplyErrors.Clear();
            knownEntities.Clear();
            visibleThisFrame.Clear();
            remoteCommitDedup.Clear();
            remoteCommitOrder.Clear();
            landedEventDedup.Clear();
            landedEventOrder.Clear();
            entityKeysToRemove.Clear();
            entityRegistry?.Clear();
        }

        public void BeginPredictedLocalAction(uint localPlayerNetId, KernelActionIntent intent)
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
                // A held trigger commits repeatedly under one action instance, so
                // one result can confirm several shots at once. Each is drawn.
                int commits = view.ApplyLocalActionResult(results[index]);
                for (int commit = 0; commit < commits; ++commit)
                {
                    TryDrawInstantShot(visual, view, view.ActionTemplateId);
                }
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
                    if (remoteEvent.event_type ==
                        KernelRemoteActionPresentationEventType.FireCommit)
                    {
                        TryDrawInstantShot(
                            visual,
                            view,
                            remoteEvent.action_template_id);
                    }
                    else if (remoteEvent.event_type ==
                        KernelRemoteActionPresentationEventType.DeathTrigger)
                    {
                        LogDeathSignal("death presentation event", remoteEvent.actor_net_id);
                    }
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

                // Read the position before the registry drops the visual: the
                // despawn is the last word anyone gets about where this entity
                // was, and removing it first throws that away.
                bool hadVisual = entityRegistry.TryGetByNetId(
                    lifecycleEvent.net_id,
                    out GameObject despawning) && despawning != null;
                Vector3 lastPosition = hadVisual
                    ? despawning.transform.position
                    : Vector3.zero;

                LogDeathSignal(
                    "despawn (" + lifecycleEvent.reason + ")",
                    lifecycleEvent.net_id,
                    "entityType=" + lifecycleEvent.entity_type +
                    " actorType=" + lifecycleEvent.actor_type +
                    " at=" + (hadVisual ? lastPosition.ToString() : "<already gone>"));

                if (entityRegistry.RemoveByNetId(
                        lifecycleEvent.net_id,
                        out ulong entityKey))
                {
                    // A corpse this client actually watched die has already been
                    // marked from the dead flag, and one it first met already
                    // dead is deliberately never marked. Either way the ground
                    // has had its answer, so the despawn must not add a second.
                    bool settledWhileAlive =
                        !knownEntities.TryGetValue(entityKey, out KnownEntity known) ||
                        !known.wasDead;

                    knownEntities.Remove(entityKey);
                    visibleThisFrame.Remove(entityKey);

                    if (hadVisual && settledWhileAlive && IsKill(lifecycleEvent))
                    {
                        TrySplatAt(lastPosition);
                    }
                }
            }
        }

        /// <summary>
        /// Draws one commit as an instant weapon's shot, if that is what the
        /// action is.
        /// </summary>
        /// <remarks>
        /// Every other kind of fire commit already has an entity behind it and is
        /// drawn from its render state, so the tracer table's own answer -- does
        /// this action template belong to a hitscan or a shotgun -- is the whole
        /// filter. The ray comes from where the actor is being drawn right now
        /// and the aim it last replicated, which is the same pair the server fired
        /// from, offset to the muzzle by the tracer component.
        /// </remarks>
        private void TryDrawInstantShot(
            GameObject visual,
            NetworkActorView view,
            uint actionTemplateId)
        {
            if (hitscanTracers == null || visual == null || actionTemplateId == 0)
            {
                return;
            }

            hitscanTracers.TryFire(
                actionTemplateId,
                visual.transform.position,
                view.WorldAimDirection);
        }

        /// <summary>
        /// Marks the ground under a dying actor, if anything is drawing splats.
        /// </summary>
        /// <remarks>
        /// Raised from the replicated dead flag rather than from the death
        /// presentation event or the despawn, because that flag is the only one
        /// of the three that is guaranteed. The presentation event belongs to an
        /// action instance and reports what an animator should trigger, so
        /// whether it arrives at all depends on what killed the actor. The
        /// despawn is worse: it carries
        /// <see cref="KernelDespawnReason"/>.Destroyed for an actor that merely
        /// went out of range as much as for one that was killed, and it arrives
        /// after <see cref="ApplyEntityLifecycleEvents"/> has already dropped the
        /// visual this reads the position off.
        /// </remarks>
        private void TrySplat(GameObject visual)
        {
            if (visual == null)
            {
                return;
            }

            TrySplatAt(visual.transform.position);
        }

        private void TrySplatAt(Vector3 position)
        {
            if (hitSplatters == null)
            {
                return;
            }

            hitSplatters.TrySplat(position);
        }

        /// <summary>
        /// Whether a despawn is an actor being killed rather than an entity
        /// merely going away.
        /// </summary>
        /// <remarks>
        /// This is a proxy, and it is the best one the lifecycle event carries.
        /// The kernel spends <see cref="KernelDespawnReason"/>.Destroyed on
        /// everything that is deliberately removed, so the reason alone would
        /// mark the ground under every expired projectile -- which is most of
        /// what despawns in a firefight. Narrowing it to actors is what makes it
        /// mean death: an actor that leaves for any other cause reports that
        /// cause instead, OutOfRange or Disconnected.
        ///
        /// It stays a proxy, though. An actor the server removes for a reason of
        /// its own -- despawning a wave, ending a round -- is indistinguishable
        /// here from one that was killed, and would leave a mark it did not earn.
        /// </remarks>
        private static bool IsKill(KernelEntityLifecycleEvent lifecycleEvent)
        {
            return lifecycleEvent.entity_type == KernelEntityType.Actor &&
                lifecycleEvent.reason == KernelDespawnReason.Destroyed;
        }

        private void LogDeathSignal(string signal, uint netId, string detail = null)
        {
            if (!logDeathSignals)
            {
                return;
            }

            Debug.Log(
                "Death signal: " + signal +
                " netId=" + netId +
                " splatters=" + (hitSplatters == null ? "<none bound>" : "bound") +
                (string.IsNullOrEmpty(detail) ? string.Empty : "  " + detail));
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
            // Carried frame to frame so death can be spotted as a transition.
            // The replicated flag says "is dead", which is true for as long as
            // the corpse is rendered; only the edge means "just died".
            public readonly bool wasDead;

            public KnownEntity(bool serverBacked, bool wasDead)
            {
                this.serverBacked = serverBacked;
                this.wasDead = wasDead;
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
