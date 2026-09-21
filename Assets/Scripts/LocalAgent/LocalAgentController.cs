using System;
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
    }

    public enum LocalAgentState { Idle, Following, Combat }

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
        private bool following;
        private bool reloadRequested;
        private bool reloadPressedLastStep;
        private bool firePressedLastStep;
        private KernelLocalWeaponState reloadWeapon;
        private uint reloadTarget;

        public void Reset()
        {
            State = LocalAgentState.Idle;
            FollowTargetId = AttackTargetId = 0;
            following = reloadRequested = reloadPressedLastStep = firePressedLastStep = false;
            reloadWeapon = default;
            reloadTarget = 0;
        }

        /// <param name="holdTrigger">The active weapon's fire action is hold-mode.
        /// Unknown weapons should pass false: re-pressing fires every weapon.</param>
        /// <param name="fireActionLive">The input sampler still tracks the fire
        /// action the last press started (it drops one the kernel refused).</param>
        public LocalAgentCommand Step(LocalAgentSettings settings,
            RenderEntityState[] states, int count, uint localPlayerId,
            bool hasWeaponState, KernelLocalWeaponState weapon, float observationAgeSeconds,
            bool holdTrigger = false, bool fireActionLive = false)
        {
            bool canPressReload = !reloadPressedLastStep;
            reloadPressedLastStep = false;
            bool fireWasPressed = firePressedLastStep;
            firePressedLastStep = false;
            if (settings == null || states == null || localPlayerId == 0 ||
                !float.IsFinite(observationAgeSeconds) || observationAgeSeconds < 0f ||
                observationAgeSeconds > 0.5f)
            {
                Reset();
                return default;
            }
            count = Mathf.Clamp(count, 0, states.Length);
            int localIndex = -1;
            for (int i = 0; i < count; i++)
                if (IsLivingActor(states[i]) && states[i].actor_type == KernelActorType.Player &&
                    states[i].net_id == localPlayerId) localIndex = i;
            if (localIndex < 0)
            {
                Reset();
                return default;
            }

            Vector3 position = Position(states[localIndex]);
            int followIndex = -1, nearestPlayer = -1, enemyIndex = -1;
            float playerDistance = float.PositiveInfinity;
            float enemyDistance = float.PositiveInfinity;
            float range = NonNegative(settings.threatRange, 15f);
            for (int i = 0; i < count; i++)
            {
                RenderEntityState candidate = states[i];
                if (!IsLivingActor(candidate) || candidate.net_id == localPlayerId) continue;
                float distance = (Position(candidate) - position).sqrMagnitude;
                if (!float.IsFinite(distance)) continue;
                if (candidate.actor_type == KernelActorType.Player)
                {
                    if (candidate.net_id == FollowTargetId) followIndex = i;
                    if (Better(states, i, nearestPlayer, distance, playerDistance))
                    { nearestPlayer = i; playerDistance = distance; }
                }
                else if (candidate.actor_type == KernelActorType.Agent &&
                    IsEnemy(settings, candidate.template_id) && distance <= range * range &&
                    Better(states, i, enemyIndex, distance, enemyDistance))
                { enemyIndex = i; enemyDistance = distance; }
            }
            if (followIndex < 0)
            {
                followIndex = nearestPlayer;
                FollowTargetId = followIndex < 0 ? 0 : states[followIndex].net_id;
                following = false;
            }

            if (enemyIndex >= 0)
            {
                State = LocalAgentState.Combat;
                following = false;
                AttackTargetId = states[enemyIndex].net_id;
                Vector3 offset = Position(states[enemyIndex]) - position + Vector3.up *
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
            State = followIndex < 0 ? LocalAgentState.Idle : LocalAgentState.Following;
            if (followIndex < 0) return default;
            Vector3 delta = Position(states[followIndex]) - position;
            Vector2 planar = new Vector2(delta.x, delta.z);
            float stop = NonNegative(settings.followStopDistance, 3f);
            float start = Mathf.Max(stop, NonNegative(settings.followStartDistance, 4f));
            if (planar.sqrMagnitude <= stop * stop) following = false;
            else if (planar.sqrMagnitude > start * start) following = true;
            return new LocalAgentCommand
            {
                Move = following ? planar.normalized : Vector2.zero,
                AimDirection = planar.sqrMagnitude > 0.000001f
                    ? new Vector3(planar.x, 0f, planar.y).normalized : Vector3.forward,
            };
        }

        private static bool Better(RenderEntityState[] states, int index, int previous,
            float distance, float bestDistance) => previous < 0 || distance < bestDistance ||
            distance == bestDistance && states[index].net_id < states[previous].net_id;
        private static bool IsEnemy(LocalAgentSettings settings, uint templateId) =>
            templateId != 0 && settings.enemyTemplateIds != null &&
            Array.IndexOf(settings.enemyTemplateIds, templateId) >= 0;
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
        private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;
        private static float NonNegative(float value, float fallback) => Mathf.Max(0f, Finite(value, fallback));
    }
}
