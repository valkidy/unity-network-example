using System.Collections.Generic;
using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.LocalAgent
{
    public enum PerceptionMode { Omniscient, Limited }
    public enum StimulusKind { Seen, Heard, Damaged }

    /// <summary>What the agent believes about one other actor.</summary>
    public struct PerceivedActor
    {
        public uint NetId;
        public KernelActorType ActorType;
        /// <summary>Known only once the actor has been seen: recognising it by its looks.</summary>
        public uint TemplateId;
        public Vector3 LastKnownPosition;
        /// <summary>Last visual confirmation; negative infinity when never seen.</summary>
        public float LastSeenTime;
        public float LastStimulusTime;
        public bool VisibleNow;
        /// <summary>Visible for long enough to react to; kept until the actor is forgotten.</summary>
        public bool Confirmed;
        public bool KnownDead;
        public StimulusKind LastKind;
    }

    /// <summary>
    /// Sits between the synchronized render states and the controller, so the
    /// controller decides only from what the agent perceives. The agent's own
    /// position is not perception and is read directly.
    /// </summary>
    /// <remarks>
    /// Omniscient: every living actor is perceived exactly where it is, which is
    /// what the controller read before this layer existed.
    ///
    /// Limited: an actor is seen when it is within visionRange and fovDegrees of
    /// the agent's facing and the line from the agent's eye reaches its feet or
    /// its head, or when it is within closeAwarenessRadius. Sight is tested at
    /// perceptionHz; between tests an actor still in view is tracked where it is.
    /// It is confirmed once seen continuously for reactionSeconds, stays
    /// confirmed while remembered, and is forgotten memorySeconds after it was
    /// last perceived. Stale records are never seen, and an actor is known dead
    /// only when it is seen dead.
    /// </remarks>
    public sealed class LocalAgentPerception
    {
        private struct Memory
        {
            public PerceivedActor Actor;
            public float VisibleSince;
        }

        private readonly List<PerceivedActor> actors = new List<PerceivedActor>();
        private readonly Dictionary<uint, Memory> memory = new Dictionary<uint, Memory>();
        private readonly List<uint> netIds = new List<uint>();
        private readonly HashSet<uint> sensed = new HashSet<uint>();
        private PerceptionMode mode;
        private float lastSenseTime = float.NegativeInfinity;

        public bool HasSelf { get; private set; }
        public uint LocalPlayerId { get; private set; }
        public Vector3 SelfPosition { get; private set; }
        public IReadOnlyList<PerceivedActor> Actors => actors;
        /// <summary>Sight tests (feet and head each count) in the last sensing pass.</summary>
        public int LastSightTests { get; private set; }

        public void Reset()
        {
            actors.Clear();
            memory.Clear();
            HasSelf = false;
            LocalPlayerId = 0;
            SelfPosition = Vector3.zero;
            lastSenseTime = float.NegativeInfinity;
            LastSightTests = 0;
        }

        /// <param name="facing">Where the agent looks; its horizontal part is used.</param>
        /// <param name="sight">Line-of-sight test for Limited mode; null means nothing blocks.</param>
        public void Update(LocalAgentSettings settings, float time,
            RenderEntityState[] states, int count, uint localPlayerId,
            Vector3 facing = default, ILineOfSight sight = null)
        {
            actors.Clear();
            HasSelf = false;
            if (settings == null || states == null || localPlayerId == 0 || !float.IsFinite(time))
            {
                LocalPlayerId = localPlayerId;
                return;
            }
            if (settings.perceptionMode != mode || localPlayerId != LocalPlayerId || time < lastSenseTime)
            {
                Reset();
                mode = settings.perceptionMode;
            }
            LocalPlayerId = localPlayerId;
            count = Mathf.Clamp(count, 0, states.Length);
            for (int i = 0; i < count; i++)
            {
                RenderEntityState state = states[i];
                if (state.net_id != localPlayerId || !IsLivingActor(state) ||
                    state.actor_type != KernelActorType.Player) continue;
                HasSelf = true;
                SelfPosition = Position(state);
            }
            if (mode == PerceptionMode.Omniscient) PerceiveEverything(states, count, time);
            else if (HasSelf) PerceiveLimited(settings, states, count, time, facing, sight);
        }

        private void PerceiveEverything(RenderEntityState[] states, int count, float time)
        {
            for (int i = 0; i < count; i++)
            {
                RenderEntityState state = states[i];
                if (!IsLivingActor(state) || state.net_id == LocalPlayerId) continue;
                actors.Add(new PerceivedActor
                {
                    NetId = state.net_id,
                    ActorType = state.actor_type,
                    TemplateId = state.template_id,
                    LastKnownPosition = Position(state),
                    LastSeenTime = time,
                    LastStimulusTime = time,
                    VisibleNow = true,
                    Confirmed = true,
                    LastKind = StimulusKind.Seen,
                });
            }
        }

        private void PerceiveLimited(LocalAgentSettings settings, RenderEntityState[] states, int count,
            float time, Vector3 facing, ILineOfSight sight)
        {
            float hz = NonNegative(settings.perceptionHz, 10f);
            bool sense = hz <= 0f || time - lastSenseTime >= 1f / hz - 1e-4f;
            if (sense)
            {
                lastSenseTime = time;
                LastSightTests = 0;
            }
            sensed.Clear();
            for (int i = 0; i < count; i++)
            {
                RenderEntityState state = states[i];
                if (!IsObservable(state) || state.net_id == LocalPlayerId) continue;
                bool known = memory.TryGetValue(state.net_id, out Memory entry);
                bool visible = sense ? CanSee(settings, state, facing, sight)
                    // Between sight tests, whatever was in view is tracked where it is.
                    : known && entry.Actor.VisibleNow;
                if (!visible) continue;
                sensed.Add(state.net_id);
                if (!known || !entry.Actor.VisibleNow) entry.VisibleSince = time;
                entry.Actor.NetId = state.net_id;
                entry.Actor.ActorType = state.actor_type;
                entry.Actor.TemplateId = state.template_id;
                entry.Actor.LastKnownPosition = Position(state);
                entry.Actor.LastSeenTime = entry.Actor.LastStimulusTime = time;
                entry.Actor.VisibleNow = true;
                entry.Actor.Confirmed |= time - entry.VisibleSince >= NonNegative(settings.reactionSeconds, 0.25f) - 1e-4f;
                entry.Actor.KnownDead = (state.visual_flags & KernelConstants.VisualFlagDead) != 0;
                entry.Actor.LastKind = StimulusKind.Seen;
                memory[state.net_id] = entry;
            }

            float memorySeconds = NonNegative(settings.memorySeconds, 8f);
            netIds.Clear();
            netIds.AddRange(memory.Keys);
            netIds.Sort();
            foreach (uint netId in netIds)
            {
                Memory entry = memory[netId];
                if (!sensed.Contains(netId))
                {
                    if (time - entry.Actor.LastStimulusTime > memorySeconds)
                    {
                        memory.Remove(netId);
                        continue;
                    }
                    entry.Actor.VisibleNow = false;
                    memory[netId] = entry;
                }
                actors.Add(entry.Actor);
            }
        }

        private bool CanSee(LocalAgentSettings settings, RenderEntityState state, Vector3 facing, ILineOfSight sight)
        {
            Vector3 target = Position(state);
            Vector3 offset = target - SelfPosition;
            float close = NonNegative(settings.closeAwarenessRadius, 2.5f);
            if (new Vector2(offset.x, offset.z).sqrMagnitude <= close * close) return true;

            Vector3 eye = SelfPosition + Vector3.up * Finite(settings.eyeHeight, 1.6f);
            float range = NonNegative(settings.visionRange, 40f);
            if ((target - eye).sqrMagnitude > range * range) return false;
            Vector2 look = new Vector2(facing.x, facing.z);
            if (!(look.sqrMagnitude > 1e-6f)) look = Vector2.up; // world forward
            Vector2 toward = new Vector2(offset.x, offset.z);
            if (toward.sqrMagnitude > 1e-6f &&
                Vector2.Angle(look, toward) > Mathf.Clamp(Finite(settings.fovDegrees, 110f), 0f, 360f) * 0.5f)
                return false;

            if (sight == null) return true;
            LastSightTests++;
            if (sight.IsClear(eye, target + Vector3.up * Finite(settings.headSampleHeight, 1.5f),
                LocalPlayerId, state.net_id)) return true;
            LastSightTests++;
            return sight.IsClear(eye, target + Vector3.up * Finite(settings.footSampleHeight, 0.5f),
                LocalPlayerId, state.net_id);
        }

        // hp is not consulted: snapshots carry it only for players, and the server sets
        // VisualFlagDead from hp == 0 in the same tick for every actor. A Stale record is
        // the kernel's fill-in for an entity missing from recent snapshots; it carries
        // no dead flag, so it cannot say whether the actor is alive.
        private static bool IsLivingActor(RenderEntityState state) => IsObservable(state) &&
            (state.visual_flags & KernelConstants.VisualFlagDead) == 0;
        // What could be looked at, dead or alive: a Stale record is not really there.
        private static bool IsObservable(RenderEntityState state) => state.net_id != 0 &&
            state.entity_type == KernelEntityType.Actor && state.status != RenderEntityStatus.Stale &&
            float.IsFinite(state.position.x) && float.IsFinite(state.position.y) && float.IsFinite(state.position.z);
        private static Vector3 Position(RenderEntityState state) =>
            new Vector3(state.position.x, state.position.y, state.position.z);
        private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;
        private static float NonNegative(float value, float fallback) => Mathf.Max(0f, Finite(value, fallback));
    }
}
