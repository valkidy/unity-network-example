using System;
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
        /// <summary>
        /// A sight line reached it at the last sight test. False for an actor
        /// noticed only by being close; the agent can shoot only what is in sight.
        /// </summary>
        public bool InSight;
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
    ///
    /// Damage to the agent (Limited only) names no attacker. It is put down to
    /// the nearest remembered enemy out of sight, which is then perceived again
    /// (kept in memory, marked Damaged); with no such enemy it is unexplained.
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
        private LocalAgentSettings settings;
        private Vector3 facing;
        private ILineOfSight sight;

        public LocalAgentPerception() => PointVisibility = CanSeePoint;

        public bool HasSelf { get; private set; }
        public uint LocalPlayerId { get; private set; }
        public Vector3 SelfPosition { get; private set; }
        public IReadOnlyList<PerceivedActor> Actors => actors;
        /// <summary>The time of the last update.</summary>
        public float Time { get; private set; }
        /// <summary>Sight lines tested since the last sensing pass, actors' and points'.</summary>
        public int LastSightTests { get; private set; }
        /// <summary><see cref="CanSeePoint"/>, allocated once.</summary>
        public Func<Vector3, bool> PointVisibility { get; }
        /// <summary>When the agent was last damaged; negative infinity if never (always when omniscient).</summary>
        public float LastDamagedTime { get; private set; } = float.NegativeInfinity;
        /// <summary>When the agent was last damaged with no remembered enemy to blame.</summary>
        public float UnexplainedDamageTime { get; private set; } = float.NegativeInfinity;

        /// <summary>How many of the events are damage to <paramref name="netId"/>.</summary>
        public static int CountDamage(KernelEvent[] events, int count, uint netId)
        {
            if (events == null || netId == 0) return 0;
            int damage = 0;
            count = Mathf.Clamp(count, 0, events.Length);
            for (int i = 0; i < count; i++)
                if (events[i].type == KernelEventType.DamageApplied && events[i].net_id == netId) damage++;
            return damage;
        }

        public void Reset()
        {
            actors.Clear();
            memory.Clear();
            HasSelf = false;
            LocalPlayerId = 0;
            SelfPosition = Vector3.zero;
            lastSenseTime = float.NegativeInfinity;
            LastSightTests = 0;
            LastDamagedTime = UnexplainedDamageTime = float.NegativeInfinity;
        }

        /// <param name="facing">Where the agent looks; its horizontal part is used.</param>
        /// <param name="sight">Line-of-sight test for Limited mode; null means nothing blocks.</param>
        /// <param name="damageTaken">Damage events to the agent since the last update.</param>
        public void Update(LocalAgentSettings settings, float time,
            RenderEntityState[] states, int count, uint localPlayerId,
            Vector3 facing = default, ILineOfSight sight = null, int damageTaken = 0)
        {
            actors.Clear();
            HasSelf = false;
            this.settings = settings;
            this.facing = facing;
            this.sight = sight;
            Time = time;
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
            else if (HasSelf)
            {
                PerceiveLimited(states, count, time);
                if (damageTaken > 0) PerceiveDamage(time);
            }
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
                    InSight = true,
                    Confirmed = true,
                    LastKind = StimulusKind.Seen,
                });
            }
        }

        private void PerceiveLimited(RenderEntityState[] states, int count, float time)
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
                bool inSight = known && entry.Actor.InSight;
                bool visible = sense ? CanSee(Position(state), state.net_id,
                        Finite(settings.headSampleHeight, 1.5f), Finite(settings.footSampleHeight, 0.5f), true, out inSight)
                    // Between sight tests, whatever was in view is tracked where it is.
                    : known && entry.Actor.VisibleNow;
                if (!visible) continue;
                entry.Actor.InSight = inSight;
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
                    entry.Actor.VisibleNow = entry.Actor.InSight = false;
                    memory[netId] = entry;
                }
                actors.Add(entry.Actor);
            }
        }

        /// <summary>
        /// Whether the agent can see a spot on the ground, as of the last update:
        /// in view and a sight line reaches 1 m above it, or close by. Always
        /// true when omniscient; false without a local player.
        /// </summary>
        public bool CanSeePoint(Vector3 point)
        {
            if (settings == null || mode == PerceptionMode.Omniscient) return true;
            return HasSelf && CanSee(point, 0, 1f, float.NaN, false, out _);
        }

        // Noticed: in view, or close. inSight: a sight line reaches it, which an
        // actor noticed only by being close may lack; tested for close targets only
        // when closeNeedsSight. A NaN second height tests one sight line only.
        private bool CanSee(Vector3 target, uint targetNetId, float firstHeight, float secondHeight,
            bool closeNeedsSight, out bool inSight)
        {
            inSight = false;
            Vector3 offset = target - SelfPosition;
            Vector3 eye = SelfPosition + Vector3.up * Finite(settings.eyeHeight, 1.6f);
            float close = NonNegative(settings.closeAwarenessRadius, 2.5f);
            if (new Vector2(offset.x, offset.z).sqrMagnitude <= close * close)
            {
                inSight = closeNeedsSight && SightReaches(eye, target, targetNetId, firstHeight, secondHeight);
                return true;
            }

            float range = NonNegative(settings.visionRange, 40f);
            if ((target - eye).sqrMagnitude > range * range) return false;
            Vector2 look = new Vector2(facing.x, facing.z);
            if (!(look.sqrMagnitude > 1e-6f)) look = Vector2.up; // world forward
            Vector2 toward = new Vector2(offset.x, offset.z);
            if (toward.sqrMagnitude > 1e-6f &&
                Vector2.Angle(look, toward) > Mathf.Clamp(Finite(settings.fovDegrees, 110f), 0f, 360f) * 0.5f)
                return false;
            inSight = SightReaches(eye, target, targetNetId, firstHeight, secondHeight);
            return inSight;
        }

        private bool SightReaches(Vector3 eye, Vector3 target, uint targetNetId, float firstHeight, float secondHeight)
        {
            if (sight == null) return true;
            LastSightTests++;
            if (sight.IsClear(eye, target + Vector3.up * firstHeight, LocalPlayerId, targetNetId)) return true;
            if (float.IsNaN(secondHeight)) return false;
            LastSightTests++;
            return sight.IsClear(eye, target + Vector3.up * secondHeight, LocalPlayerId, targetNetId);
        }

        // The event names no attacker: blame the nearest remembered enemy out of sight.
        private void PerceiveDamage(float time)
        {
            LastDamagedTime = time;
            int blamed = -1;
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < actors.Count; i++)
            {
                PerceivedActor actor = actors[i];
                if (actor.InSight || actor.KnownDead || actor.ActorType != KernelActorType.Agent ||
                    settings.enemyTemplateIds == null || actor.TemplateId == 0 ||
                    Array.IndexOf(settings.enemyTemplateIds, actor.TemplateId) < 0) continue;
                float distance = (actor.LastKnownPosition - SelfPosition).sqrMagnitude;
                if (distance < nearest) { nearest = distance; blamed = i; }
            }
            if (blamed < 0)
            {
                UnexplainedDamageTime = time;
                return;
            }
            PerceivedActor attacker = actors[blamed];
            attacker.LastStimulusTime = time;
            attacker.LastKind = StimulusKind.Damaged;
            actors[blamed] = attacker;
            Memory entry = memory[attacker.NetId];
            entry.Actor = attacker;
            memory[attacker.NetId] = entry;
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
