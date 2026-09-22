using System.Collections.Generic;
using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.LocalAgent
{
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
        /// <summary>Visible for long enough to react to.</summary>
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
    /// </remarks>
    public sealed class LocalAgentPerception
    {
        private readonly List<PerceivedActor> actors = new List<PerceivedActor>();

        public bool HasSelf { get; private set; }
        public uint LocalPlayerId { get; private set; }
        public Vector3 SelfPosition { get; private set; }
        public IReadOnlyList<PerceivedActor> Actors => actors;

        public void Reset()
        {
            actors.Clear();
            HasSelf = false;
            LocalPlayerId = 0;
            SelfPosition = Vector3.zero;
        }

        public void Update(LocalAgentSettings settings, float time,
            RenderEntityState[] states, int count, uint localPlayerId)
        {
            actors.Clear();
            HasSelf = false;
            LocalPlayerId = localPlayerId;
            if (settings == null || states == null || localPlayerId == 0) return;
            count = Mathf.Clamp(count, 0, states.Length);
            for (int i = 0; i < count; i++)
            {
                RenderEntityState state = states[i];
                if (!IsLivingActor(state)) continue;
                if (state.net_id == localPlayerId)
                {
                    if (state.actor_type != KernelActorType.Player) continue;
                    HasSelf = true;
                    SelfPosition = Position(state);
                    continue;
                }
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

        // hp is not consulted: snapshots carry it only for players, and the server sets
        // VisualFlagDead from hp == 0 in the same tick for every actor. A Stale record is
        // the kernel's fill-in for an entity missing from recent snapshots; it carries
        // no dead flag, so it cannot say whether the actor is alive.
        private static bool IsLivingActor(RenderEntityState state) => state.net_id != 0 &&
            state.entity_type == KernelEntityType.Actor && state.status != RenderEntityStatus.Stale &&
            (state.visual_flags & KernelConstants.VisualFlagDead) == 0 &&
            float.IsFinite(state.position.x) && float.IsFinite(state.position.y) && float.IsFinite(state.position.z);
        private static Vector3 Position(RenderEntityState state) =>
            new Vector3(state.position.x, state.position.y, state.position.z);
    }
}
