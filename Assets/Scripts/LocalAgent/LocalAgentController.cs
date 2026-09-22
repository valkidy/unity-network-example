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
        [Tooltip("Cells whose centre is this close count as seen; with Limited perception, " +
            "only those the agent can also see.")]
        [Min(0f)] public float sightRadius = 6f;
        [Tooltip("With Limited perception, cells tested for sight per input tick at most.")]
        [Min(0)] public int explorationSightChecks = 16;
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
        [Tooltip("Seconds the agent takes to look all around: where a remembered enemy was, " +
            "when hit, and while exploring.")]
        [Min(0.1f)] public float investigateScanSeconds = 1f;
        [Tooltip("Limited only: seconds of exploring between look-arounds, taken while walking. 0 never looks around.")]
        [Min(0f)] public float exploreLookAroundSeconds = 4f;
        [Tooltip("Limited only: random aim error when the agent starts aiming at a target, in degrees.")]
        [Min(0f)] public float aimErrorStartDegrees = 6f;
        [Tooltip("Limited only: the aim error once the agent has aimed at the same target for aimSettleSeconds.")]
        [Min(0f)] public float aimErrorSettledDegrees = 1.5f;
        [Min(0f)] public float aimSettleSeconds = 1f;
    }

    public enum LocalAgentState { Idle, Following, Combat, Exploring, Investigating }

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
        /// <summary>The remembered enemy whose last known position is being checked, or 0.</summary>
        public uint InvestigateTargetId { get; private set; }
        /// <summary>Draws the aim error; replace it with a seeded one for repeatable runs.</summary>
        public System.Random Random { get; set; } = new System.Random();
        /// <summary>The last non-zero aim the agent sent: where it is looking.</summary>
        public Vector3 LastAimDirection { get; private set; } = Vector3.forward;
        private bool following;
        private bool reloadRequested;
        private bool reloadPressedLastStep;
        private bool firePressedLastStep;
        private KernelLocalWeaponState reloadWeapon;
        private uint reloadTarget;
        private LocalAgentExplorer explorer;
        private LocalAgentPathFollower investigationPath;
        private Vector3 investigateGoal;
        private bool investigatePlanned;
        private float scanStart = float.NaN;
        private float scanFromDegrees;
        private bool damageScan;
        private float handledDamageTime = float.NegativeInfinity;
        private float aimingSince;
        private float fireReadyAt = float.NegativeInfinity;
        private float lastExploreTime = float.NegativeInfinity;
        private float nextLookAround = float.NaN;
        private float lookAroundStart = float.NaN;
        private float lookAroundFromDegrees;
        // Memories already investigated, by the time they were last perceived:
        // seeing the enemy again, or being hit by it, makes it worth another look.
        private readonly Dictionary<uint, float> investigated = new Dictionary<uint, float>();
        private readonly List<uint> forgottenInvestigations = new List<uint>();

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
            StopInvestigating();
            investigated.Clear();
            damageScan = false;
            handledDamageTime = float.NegativeInfinity;
            fireReadyAt = float.NegativeInfinity;
            lastExploreTime = float.NegativeInfinity;
            nextLookAround = lookAroundStart = float.NaN;
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
            int followIndex = -1, nearestPlayer = -1, enemyIndex = -1, rememberedIndex = -1, currentIndex = -1;
            float playerDistance = float.PositiveInfinity;
            float enemyDistance = float.PositiveInfinity;
            float rememberedDistance = float.PositiveInfinity;
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
                else if (candidate.ActorType == KernelActorType.Agent && candidate.Confirmed &&
                    IsEnemy(settings, candidate.TemplateId) && distance <= range * range)
                {
                    // Only a threat in sight, that the agent has had time to react to, is shot at.
                    if (candidate.VisibleNow && candidate.InSight)
                    {
                        if (candidate.NetId == AttackTargetId) currentIndex = i;
                        if (Better(actors, i, enemyIndex, distance, enemyDistance))
                        { enemyIndex = i; enemyDistance = distance; }
                    }
                    // One out of sight is looked for, the most recently perceived first.
                    else if (!(investigated.TryGetValue(candidate.NetId, out float perceivedAt) &&
                        candidate.LastStimulusTime <= perceivedAt) &&
                        MoreRecent(actors, i, rememberedIndex, distance, rememberedDistance))
                    { rememberedIndex = i; rememberedDistance = distance; }
                }
            }
            ForgetInvestigationsOf(actors);
            if (followIndex < 0)
            {
                followIndex = nearestPlayer;
                FollowTargetId = followIndex < 0 ? 0 : actors[followIndex].NetId;
                following = false;
            }

            bool limited = settings.perceptionMode == PerceptionMode.Limited;
            // A person keeps shooting at the enemy they are on while it stays in sight.
            if (limited && currentIndex >= 0) enemyIndex = currentIndex;
            if (enemyIndex >= 0)
            {
                StopInvestigating();
                damageScan = false;
                State = LocalAgentState.Combat;
                following = false;
                uint target = actors[enemyIndex].NetId;
                if (target != AttackTargetId)
                {
                    // Turning to another target takes a new reaction; the first one
                    // was already reacted to when perception confirmed it.
                    fireReadyAt = limited && AttackTargetId != 0
                        ? perception.Time + NonNegative(settings.reactionSeconds, 0.25f) : float.NegativeInfinity;
                    aimingSince = perception.Time;
                    AttackTargetId = target;
                }
                Vector3 offset = actors[enemyIndex].LastKnownPosition - position + Vector3.up *
                    (Finite(settings.targetHeight, 0.5f) - Finite(settings.muzzleHeight, 0.5f));
                Vector3 aim = offset.sqrMagnitude > 0.000001f ? offset.normalized : Vector3.forward;
                if (limited) aim = WithAimError(settings, aim, perception.Time - aimingSince);
                LocalAgentCommand command = new LocalAgentCommand { Aim = true, AimDirection = aim };
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
                    if (perception.Time < fireReadyAt - 1e-4f) return command; // still turning onto it
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
            if (rememberedIndex >= 0 &&
                TryInvestigate(settings, perception, actors[rememberedIndex], position, out LocalAgentCommand search))
            {
                following = false;
                activeExplorer?.ClearPath();
                return search;
            }
            if (rememberedIndex < 0 && InvestigateTargetId != 0) StopInvestigating();
            // Hit by something it cannot place: look all around where it stands.
            if (perception.UnexplainedDamageTime > handledDamageTime)
            {
                handledDamageTime = perception.UnexplainedDamageTime;
                if (!damageScan) StopInvestigating();
                damageScan = true;
            }
            if (damageScan)
            {
                if (TryScan(settings, perception, out LocalAgentCommand look))
                {
                    State = LocalAgentState.Investigating;
                    following = false;
                    activeExplorer?.ClearPath();
                    return look;
                }
                damageScan = false;
                StopInvestigating();
            }
            if (followIndex < 0)
            {
                following = false;
                if (activeExplorer == null)
                {
                    State = LocalAgentState.Idle;
                    return default;
                }
                return Explore(activeExplorer, settings, perception, position, null, 0f);
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
                return Explore(activeExplorer, settings, perception, position, followPosition, leash);

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
            LocalAgentPerception perception, Vector3 position, Vector3? anchor, float leash)
        {
            State = LocalAgentState.Exploring;
            if (settings.perceptionMode == PerceptionMode.Limited)
                activeExplorer.MarkSeen(position, NonNegative(settings.sightRadius, 6f),
                    perception.PointVisibility, Mathf.Max(0, settings.explorationSightChecks));
            else
                activeExplorer.MarkSeen(position, NonNegative(settings.sightRadius, 6f));
            Vector2 move = activeExplorer.Step(position, anchor, leash);
            Vector3 aim = move.sqrMagnitude > 0.000001f ? new Vector3(move.x, 0f, move.y) : Vector3.forward;
            if (settings.perceptionMode == PerceptionMode.Limited)
                aim = LookAroundWhileExploring(settings, perception.Time, aim);
            return new LocalAgentCommand { Move = move, AimDirection = aim };
        }

        /// <summary>
        /// Every exploreLookAroundSeconds of exploring, turns the aim a full circle
        /// in investigateScanSeconds while the agent keeps walking; otherwise it
        /// looks where it walks. Coming back to exploring starts the wait over.
        /// </summary>
        private Vector3 LookAroundWhileExploring(LocalAgentSettings settings, float time, Vector3 walkAim)
        {
            float interval = NonNegative(settings.exploreLookAroundSeconds, 4f);
            bool resumed = time - lastExploreTime > 0.5f; // it did something else in between
            lastExploreTime = time;
            if (!(interval > 0f)) return walkAim;
            if (resumed || float.IsNaN(nextLookAround))
            {
                nextLookAround = time + interval;
                lookAroundStart = float.NaN;
            }
            if (float.IsNaN(lookAroundStart))
            {
                if (time < nextLookAround) return walkAim;
                lookAroundStart = time;
                lookAroundFromDegrees = Yaw(walkAim);
            }
            float progress = (time - lookAroundStart) / Mathf.Max(0.1f, Finite(settings.investigateScanSeconds, 1f));
            if (progress < 1f) return CircleAim(lookAroundFromDegrees, progress);
            lookAroundStart = float.NaN;
            nextLookAround = time + interval;
            return walkAim;
        }

        /// <summary>
        /// Walks to where a remembered enemy was last seen, facing that spot, then
        /// looks all around. False once the look-around is done (or the memory is
        /// gone), so the agent goes on with what it was doing.
        /// </summary>
        private bool TryInvestigate(LocalAgentSettings settings, LocalAgentPerception perception,
            PerceivedActor target, Vector3 position, out LocalAgentCommand command)
        {
            command = default;
            damageScan = false;
            // A target still being noticed moves its last known position a little every
            // step; only a new target or a real move starts over.
            if (target.NetId != InvestigateTargetId ||
                (target.LastKnownPosition - investigateGoal).sqrMagnitude > 1f)
            {
                StopInvestigating();
                InvestigateTargetId = target.NetId;
                investigateGoal = target.LastKnownPosition;
            }
            State = LocalAgentState.Investigating;
            Vector3 toGoal = investigateGoal - position;
            var planar = new Vector2(toGoal.x, toGoal.z);
            float reach = Mathf.Max(0.1f, Finite(settings.waypointReachDistance, 0.75f));

            if (float.IsNaN(scanStart))
            {
                Vector2 move = Vector2.zero;
                bool arrived = planar.magnitude <= reach;
                if (!arrived && NavMesh != null)
                {
                    LocalAgentPathFollower path = PathFor(settings);
                    if (!investigatePlanned)
                    {
                        investigatePlanned = true;
                        path.TryPlan(position, investigateGoal);
                    }
                    // No route, arrived, or stuck: look around from here.
                    arrived = path.Step(position, out move) != PathProgress.Walking;
                }
                else if (!arrived)
                    move = planar.normalized;
                if (!arrived)
                {
                    command.Move = move;
                    command.AimDirection = planar.sqrMagnitude > 0.000001f
                        ? new Vector3(planar.x, 0f, planar.y).normalized
                        : new Vector3(move.x, 0f, move.y);
                    return true;
                }
            }
            if (TryScan(settings, perception, out command)) return true;
            investigated[target.NetId] = target.LastStimulusTime;
            StopInvestigating();
            return false;
        }

        /// <summary>
        /// Turns a full circle on the spot in investigateScanSeconds, starting
        /// from where the agent looks; false once the circle is done.
        /// </summary>
        private bool TryScan(LocalAgentSettings settings, LocalAgentPerception perception,
            out LocalAgentCommand command)
        {
            command = default;
            if (float.IsNaN(scanStart))
            {
                scanStart = perception.Time;
                scanFromDegrees = Yaw(LastAimDirection);
            }
            float scanSeconds = Mathf.Max(0.1f, Finite(settings.investigateScanSeconds, 1f));
            float progress = (perception.Time - scanStart) / scanSeconds;
            if (!(progress < 1f)) return false;
            command.AimDirection = CircleAim(scanFromDegrees, progress);
            return true;
        }

        private static float Yaw(Vector3 direction) => Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        // The aim a fraction of the way round a full turn that started at fromDegrees.
        private static Vector3 CircleAim(float fromDegrees, float progress)
        {
            float yaw = (fromDegrees + 360f * Mathf.Max(0f, progress)) * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
        }

        /// <summary>
        /// Turns the aim by a random angle within the current error, which shrinks
        /// from aimErrorStartDegrees to aimErrorSettledDegrees over aimSettleSeconds
        /// of aiming at the same target.
        /// </summary>
        private Vector3 WithAimError(LocalAgentSettings settings, Vector3 direction, float aimedSeconds)
        {
            float settle = NonNegative(settings.aimSettleSeconds, 1f);
            float settled = settle > 0f ? Mathf.Clamp01(aimedSeconds / settle) : 1f;
            float error = Mathf.Lerp(NonNegative(settings.aimErrorStartDegrees, 6f),
                NonNegative(settings.aimErrorSettledDegrees, 1.5f), settled) * Mathf.Deg2Rad;
            if (!(error > 0f) || Random == null) return direction;
            // Uniform over the disc of that radius, around the aim.
            float angle = (float)(Random.NextDouble() * 2.0 * Math.PI);
            float radius = error * Mathf.Sqrt((float)Random.NextDouble());
            Vector3 right = Vector3.Cross(Vector3.up, direction);
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            right.Normalize();
            Vector3 up = Vector3.Cross(direction, right);
            return (direction + right * Mathf.Tan(radius * Mathf.Cos(angle)) +
                up * Mathf.Tan(radius * Mathf.Sin(angle))).normalized;
        }

        private void StopInvestigating()
        {
            InvestigateTargetId = 0;
            investigatePlanned = false;
            scanStart = float.NaN;
            investigationPath?.Clear();
        }

        private LocalAgentPathFollower PathFor(LocalAgentSettings settings)
        {
            if (investigationPath == null || investigationPath.Query != NavMesh)
                investigationPath = new LocalAgentPathFollower(NavMesh);
            investigationPath.StuckSteps = Mathf.Max(1, settings.stuckSteps);
            investigationPath.WaypointReachDistance = Mathf.Max(0.1f, Finite(settings.waypointReachDistance, 0.75f));
            return investigationPath;
        }

        // Drops investigations of enemies the agent no longer remembers.
        private void ForgetInvestigationsOf(IReadOnlyList<PerceivedActor> actors)
        {
            if (investigated.Count == 0) return;
            forgottenInvestigations.Clear();
            foreach (uint netId in investigated.Keys)
            {
                bool remembered = false;
                for (int i = 0; i < actors.Count && !remembered; i++) remembered = actors[i].NetId == netId;
                if (!remembered) forgottenInvestigations.Add(netId);
            }
            foreach (uint netId in forgottenInvestigations) investigated.Remove(netId);
        }

        private static bool MoreRecent(IReadOnlyList<PerceivedActor> actors, int index, int previous,
            float distance, float bestDistance) => previous < 0 ||
            actors[index].LastSeenTime > actors[previous].LastSeenTime ||
            actors[index].LastSeenTime == actors[previous].LastSeenTime &&
            Better(actors, index, previous, distance, bestDistance);
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
