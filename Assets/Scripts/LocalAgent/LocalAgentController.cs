using System;
using System.Collections.Generic;
using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.LocalAgent
{
    [Serializable]
    public sealed class LocalAgentSettings
    {
        [Min(0f)] public float followStartDistance = 4f;
        [Min(0f)] public float followStopDistance = 3f;
        [Min(0f)] public float threatRange = 15f;
        public float muzzleHeight = 0.5f;
        public float targetHeight = 0.5f;
        [Tooltip("Weapon id the agent switches to (0 = rifle). -1 keeps the current weapon.")]
        public int preferredWeaponId = 0;
        [Tooltip("Only Agent actors with one of these template IDs are hostile. Empty means follow only.")]
        public uint[] enemyTemplateIds = Array.Empty<uint>();

        [Header("Exploration (needs the catalog's navigation mesh)")]
        public bool enableExploration = true;
        [Tooltip("With a player to follow, explore only within this distance of them; go back " +
            "to them beyond it. Without one, the whole navigation mesh is explored.")]
        [Min(0f)] public float leashRadius = 15f;
        [Tooltip("Side of the square cells the walkable area is split into for exploration.")]
        [Min(0.5f)] public float explorationCellSize = 4f;
        [Tooltip("Cells whose centre is this close count as seen. No line-of-sight test.")]
        [Min(0f)] public float sightRadius = 6f;
        [Min(0.1f)] public float waypointReachDistance = 0.75f;
        [Tooltip("Input ticks without 0.5 m of progress before a target is set aside (30 Hz by default).")]
        [Min(1)] public int stuckSteps = 45;

        [Header("Perception")]
        [Tooltip("Omniscient reads every synchronized actor; Limited only what the agent can see.")]
        public PerceptionMode perceptionMode = PerceptionMode.Omniscient;
        [Tooltip("Sight tests per second in Limited mode. Actors in view are tracked in between.")]
        [Min(0f)] public float perceptionHz = 10f;
        [Min(0f)] public float eyeHeight = 1.6f;
        [Tooltip("The synchronized range is about 44 m.")]
        [Min(0f)] public float visionRange = 40f;
        [Tooltip("Horizontal field of view around where the agent last aimed.")]
        [Range(0f, 360f)] public float fovDegrees = 110f;
        [Tooltip("Actors this close are noticed without being looked at or in sight.")]
        [Min(0f)] public float closeAwarenessRadius = 2.5f;
        [Tooltip("Sight lines go to these heights above a target's feet; either one clear is enough.")]
        public float headSampleHeight = 1.5f;
        public float footSampleHeight = 0.5f;
        [Tooltip("Seconds an actor must stay in sight before the agent reacts to it.")]
        [Min(0f)] public float reactionSeconds = 0.25f;
        [Tooltip("Seconds an actor out of sight is remembered.")]
        [Min(0f)] public float memorySeconds = 8f;
        [Tooltip("Unity physics layers that block sight: the terrain. Props and actors " +
            "block it through the kernel's collider shapes.")]
        public LayerMask sightBlockingLayers = 1; // Default, the terrain's layer
    }

    public enum LocalAgentState { Idle, Following, Combat, Exploring }

    /// <summary>Device-independent command; Move is world X/Z, AimDirection is world space.</summary>
    public struct LocalAgentCommand
    {
        public Vector2 Move;
        public Vector3 AimDirection;
        public bool Aim;
        public bool Fire;
        public bool Reload;
    }

    /// <summary>
    /// Pure client-side decision maker. It never reads devices or submits network input.
    /// Step is called only when the returned command will be submitted.
    /// </summary>
    public sealed class LocalAgentController
    {
        public LocalAgentState State { get; private set; }
        public uint FollowTargetId { get; private set; }
        public uint AttackTargetId { get; private set; }
        /// <summary>The last non-zero aim the agent sent: where it is looking.</summary>
        public Vector3 LastAimDirection { get; private set; } = Vector3.forward;
        private bool following;
        private bool reloadRequested;
        private bool reloadPressedLastStep;
        private bool firePressedLastStep;
        private KernelLocalWeaponState reloadWeapon;
        private uint reloadTarget;
        private LocalAgentExplorer explorer;

        /// <summary>
        /// The walkable area to explore. Null (the default) disables exploration,
        /// and the agent only follows and fights.
        /// </summary>
        public DetourNavMeshQuery NavMesh { get; set; }
        /// <summary>The explorer in use, for inspection; null until exploration first runs.</summary>
        public LocalAgentExplorer Explorer => explorer;

        public void Reset()
        {
            State = LocalAgentState.Idle;
            FollowTargetId = AttackTargetId = 0;
            following = reloadRequested = reloadPressedLastStep = firePressedLastStep = false;
            reloadWeapon = default;
            reloadTarget = 0;
            explorer?.ClearPath(); // cells already seen stay seen
        }

        /// <param name="perception">What the agent perceives, updated from the
        /// observation <paramref name="observationAgeSeconds"/> describes.</param>
        /// <param name="holdTrigger">The active weapon's fire action is hold-mode.
        /// Unknown weapons should pass false: re-pressing fires every weapon.</param>
        /// <param name="fireActionLive">The input sampler still tracks the fire
        /// action the last press started (it drops one the kernel refused).</param>
        public LocalAgentCommand Step(LocalAgentSettings settings, LocalAgentPerception perception,
            bool hasWeaponState, KernelLocalWeaponState weapon, float observationAgeSeconds,
            bool holdTrigger = false, bool fireActionLive = false)
        {
            LocalAgentCommand command = Decide(settings, perception, hasWeaponState, weapon,
                observationAgeSeconds, holdTrigger, fireActionLive);
            if (command.AimDirection.sqrMagnitude > 0.000001f)
                LastAimDirection = command.AimDirection.normalized;
            return command;
        }

        private LocalAgentCommand Decide(LocalAgentSettings settings, LocalAgentPerception perception,
            bool hasWeaponState, KernelLocalWeaponState weapon, float observationAgeSeconds,
            bool holdTrigger, bool fireActionLive)
        {
            bool canPressReload = !reloadPressedLastStep;
            reloadPressedLastStep = false;
            bool fireWasPressed = firePressedLastStep;
            firePressedLastStep = false;
            if (settings == null || perception == null || !perception.HasSelf ||
                !float.IsFinite(observationAgeSeconds) || observationAgeSeconds < 0f ||
                observationAgeSeconds > 0.5f)
            {
                Reset();
                return default;
            }

            IReadOnlyList<PerceivedActor> actors = perception.Actors;
            Vector3 position = perception.SelfPosition;
            int followIndex = -1, nearestPlayer = -1, enemyIndex = -1;
            float playerDistance = float.PositiveInfinity;
            float enemyDistance = float.PositiveInfinity;
            float range = NonNegative(settings.threatRange, 15f);
            for (int i = 0; i < actors.Count; i++)
            {
                PerceivedActor candidate = actors[i];
                if (candidate.KnownDead || candidate.NetId == 0 ||
                    candidate.NetId == perception.LocalPlayerId) continue;
                float distance = (candidate.LastKnownPosition - position).sqrMagnitude;
                if (!float.IsFinite(distance)) continue;
                if (candidate.ActorType == KernelActorType.Player)
                {
                    if (candidate.NetId == FollowTargetId) followIndex = i;
                    if (Better(actors, i, nearestPlayer, distance, playerDistance))
                    { nearestPlayer = i; playerDistance = distance; }
                }
                // Only a threat the agent can see, and has had time to react to, is shot at.
                else if (candidate.ActorType == KernelActorType.Agent &&
                    candidate.VisibleNow && candidate.Confirmed &&
                    IsEnemy(settings, candidate.TemplateId) && distance <= range * range &&
                    Better(actors, i, enemyIndex, distance, enemyDistance))
                { enemyIndex = i; enemyDistance = distance; }
            }
            if (followIndex < 0)
            {
                followIndex = nearestPlayer;
                FollowTargetId = followIndex < 0 ? 0 : actors[followIndex].NetId;
                following = false;
            }

            if (enemyIndex >= 0)
            {
                State = LocalAgentState.Combat;
                following = false;
                AttackTargetId = actors[enemyIndex].NetId;
                Vector3 offset = actors[enemyIndex].LastKnownPosition - position + Vector3.up *
                    (Finite(settings.targetHeight, 0.5f) - Finite(settings.muzzleHeight, 0.5f));
                LocalAgentCommand command = new LocalAgentCommand
                { Aim = true, AimDirection = offset.sqrMagnitude > 0.000001f ? offset.normalized : Vector3.forward };
                bool validWeapon = hasWeaponState &&
                    (weapon.flags & KernelConstants.LocalWeaponStateFlagWeaponIdValid) != 0;
                if (!validWeapon) return command;
                if (reloadRequested && (reloadTarget != AttackTargetId ||
                    reloadWeapon.weapon_id != weapon.weapon_id ||
                    reloadWeapon.active_weapon_slot != weapon.active_weapon_slot ||
                    reloadWeapon.ammo != weapon.ammo ||
                    reloadWeapon.authoritative_ammo != weapon.authoritative_ammo ||
                    reloadWeapon.flags != weapon.flags)) reloadRequested = false;
                // authoritative_tick is deliberately excluded: time passing is not a weapon change.
                if ((weapon.flags & KernelConstants.LocalWeaponStateFlagReloading) != 0)
                    return command;
                if (weapon.ammo > 0)
                {
                    reloadRequested = false;
                    // A press action fires once per press, and a refused hold action
                    // needs a fresh press too, so release for one step between them.
                    command.Fire = !fireWasPressed || holdTrigger && fireActionLive;
                    firePressedLastStep = command.Fire;
                }
                else if (!reloadRequested && canPressReload)
                {
                    command.Reload = true;
                    reloadPressedLastStep = true;
                    reloadRequested = true;
                    reloadWeapon = weapon;
                    reloadTarget = AttackTargetId;
                }
                return command;
            }

            AttackTargetId = 0;
            reloadRequested = false;
            LocalAgentExplorer activeExplorer = ExplorerFor(settings);
            if (followIndex < 0)
            {
                following = false;
                if (activeExplorer == null)
                {
                    State = LocalAgentState.Idle;
                    return default;
                }
                return Explore(activeExplorer, settings, position, null, 0f);
            }

            Vector3 followPosition = actors[followIndex].LastKnownPosition;
            Vector3 delta = followPosition - position;
            Vector2 planar = new Vector2(delta.x, delta.z);
            float stop = NonNegative(settings.followStopDistance, 3f);
            // An exploring agent roams the leash and only comes back beyond it.
            float leash = Mathf.Max(stop, NonNegative(settings.leashRadius, 15f));
            float start = activeExplorer != null ? leash
                : Mathf.Max(stop, NonNegative(settings.followStartDistance, 4f));
            if (planar.sqrMagnitude <= stop * stop) following = false;
            else if (planar.sqrMagnitude > start * start) following = true;
            if (activeExplorer != null && !following)
                return Explore(activeExplorer, settings, position, followPosition, leash);

            activeExplorer?.ClearPath();
            State = LocalAgentState.Following;
            return new LocalAgentCommand
            {
                Move = following ? planar.normalized : Vector2.zero,
                AimDirection = planar.sqrMagnitude > 0.000001f
                    ? new Vector3(planar.x, 0f, planar.y).normalized : Vector3.forward,
            };
        }

        private LocalAgentExplorer ExplorerFor(LocalAgentSettings settings)
        {
            if (!settings.enableExploration || NavMesh == null) return null;
            float cellSize = Mathf.Max(0.5f, Finite(settings.explorationCellSize, 4f));
            if (explorer == null || explorer.Query != NavMesh || explorer.CellSize != cellSize)
                explorer = new LocalAgentExplorer(NavMesh, cellSize);
            explorer.StuckSteps = Mathf.Max(1, settings.stuckSteps);
            explorer.WaypointReachDistance = Mathf.Max(0.1f, Finite(settings.waypointReachDistance, 0.75f));
            return explorer;
        }

        private LocalAgentCommand Explore(LocalAgentExplorer activeExplorer, LocalAgentSettings settings,
            Vector3 position, Vector3? anchor, float leash)
        {
            State = LocalAgentState.Exploring;
            activeExplorer.MarkSeen(position, NonNegative(settings.sightRadius, 6f));
            Vector2 move = activeExplorer.Step(position, anchor, leash);
            return new LocalAgentCommand
            {
                Move = move,
                AimDirection = move.sqrMagnitude > 0.000001f
                    ? new Vector3(move.x, 0f, move.y) : Vector3.forward,
            };
        }

        private static bool Better(IReadOnlyList<PerceivedActor> actors, int index, int previous,
            float distance, float bestDistance) => previous < 0 || distance < bestDistance ||
            distance == bestDistance && actors[index].NetId < actors[previous].NetId;
        private static bool IsEnemy(LocalAgentSettings settings, uint templateId) =>
            templateId != 0 && settings.enemyTemplateIds != null &&
            Array.IndexOf(settings.enemyTemplateIds, templateId) >= 0;
        private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;
        private static float NonNegative(float value, float fallback) => Mathf.Max(0f, Finite(value, fallback));
    }
}
