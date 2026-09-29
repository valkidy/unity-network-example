using System.Collections.Generic;
using NetworkExample.Kernel;
using NetworkExample.UnityDemo.UI;
using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    /// <summary>
    /// Works out where a targeted strike would land while one is in hand, and
    /// tells the reticle whether the cast would be accepted. The landing point
    /// itself is only drawn by <see cref="NetworkDebugView"/>.
    /// </summary>
    /// <remarks>
    /// The landing point comes from <see cref="TargetedStrikeTargeting"/>, which
    /// runs the server's two rays against what Unity can see. It is a preview:
    /// the server decides where the strike really lands, and nothing here is
    /// predicted or spawned ahead of it.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class TargetedStrikePreview : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("What counts as the world for the preview's rays.")]
        private LayerMask worldMask = ~0;

        [SerializeField]
        [Tooltip(
            "Stand-in hit volume for other actors, as a box centred this high " +
            "above their position. The player template's hitbox.")]
        private Vector3 actorBoxCenter = new Vector3(0f, 0.9f, 0f);

        [SerializeField]
        private Vector3 actorBoxHalfExtents = new Vector3(0.35f, 0.9f, 0.35f);

        private readonly Dictionary<byte, float> maxRangeByWeaponId =
            new Dictionary<byte, float>();
        private readonly List<TargetedStrikeTargeting.ActorBox> actorBoxes =
            new List<TargetedStrikeTargeting.ActorBox>();
        private AimReticleView reticle;
        private TargetedStrikeTargeting.WorldRaycast worldRaycast;

        public bool IsActive { get; private set; }
        public bool HasTarget { get; private set; }
        public Vector3 Landing { get; private set; }

        public void Configure(AimReticleView aimReticle)
        {
            reticle = aimReticle;
        }

        /// <summary>
        /// The targeted strike weapons this session's catalog declares, by weapon
        /// id, with their <c>max_range</c>. Every other weapon hides the preview.
        /// </summary>
        public void ConfigureWeapons(IReadOnlyDictionary<byte, float> maxRanges)
        {
            maxRangeByWeaponId.Clear();
            if (maxRanges == null)
            {
                return;
            }

            foreach (KeyValuePair<byte, float> pair in maxRanges)
            {
                maxRangeByWeaponId[pair.Key] = pair.Value;
            }
        }

        public bool IsTargetedStrike(byte weaponId) =>
            maxRangeByWeaponId.ContainsKey(weaponId);

        /// <summary>
        /// Refreshes the preview for this frame. Pass <paramref name="canAim"/>
        /// false while there is no local player to aim from (not spawned, dead).
        /// </summary>
        public void UpdatePreview(
            bool canAim,
            byte weaponId,
            Vector3 fireOrigin,
            Vector3 aimDirection,
            RenderEntityState[] states,
            int count,
            uint localPlayerNetId)
        {
            float maxRange = 0f;
            IsActive = canAim &&
                maxRangeByWeaponId.TryGetValue(weaponId, out maxRange);
            if (!IsActive)
            {
                HasTarget = false;
                Landing = Vector3.zero;
                reticle?.SetTargetState(AimReticleView.TargetState.None);
                return;
            }

            CollectActorBoxes(states, count, localPlayerNetId);
            if (worldRaycast == null)
            {
                worldRaycast = TargetedStrikeTargeting.PhysicsRaycast(worldMask);
            }

            HasTarget = TargetedStrikeTargeting.TryResolve(
                fireOrigin,
                aimDirection,
                maxRange,
                worldRaycast,
                actorBoxes,
                out Vector3 landing);
            Landing = HasTarget ? landing : Vector3.zero;
            reticle?.SetTargetState(HasTarget
                ? AimReticleView.TargetState.Valid
                : AimReticleView.TargetState.Invalid);
        }

        public void Hide()
        {
            IsActive = false;
            HasTarget = false;
            Landing = Vector3.zero;
            reticle?.SetTargetState(AimReticleView.TargetState.None);
        }

        private void OnDisable()
        {
            Hide();
        }

        private void CollectActorBoxes(
            RenderEntityState[] states,
            int count,
            uint localPlayerNetId)
        {
            actorBoxes.Clear();
            if (states == null)
            {
                return;
            }

            int safeCount = Mathf.Clamp(count, 0, states.Length);
            for (int index = 0; index < safeCount; ++index)
            {
                RenderEntityState state = states[index];
                if (state.entity_type != KernelEntityType.Actor ||
                    state.net_id == 0 ||
                    state.net_id == localPlayerNetId ||
                    (state.visual_flags & KernelConstants.VisualFlagDead) != 0)
                {
                    continue;
                }

                Vector3 position = new Vector3(
                    state.position.x,
                    state.position.y,
                    state.position.z);
                actorBoxes.Add(new TargetedStrikeTargeting.ActorBox(
                    position + actorBoxCenter,
                    actorBoxHalfExtents));
            }
        }
    }
}
