using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.Items
{
    public static class ItemPropTargetSelector
    {
        public static bool TrySelectPickupTarget(
            RenderEntityState[] states,
            int count,
            uint localPlayerNetId,
            Vector3 viewForward,
            float maximumRange,
            out RenderEntityState target)
        {
            target = default;
            if (states == null || localPlayerNetId == 0 || maximumRange <= 0f)
            {
                return false;
            }

            int safeCount = Mathf.Clamp(count, 0, states.Length);
            if (!TryFindLocalPlayerPosition(
                    states,
                    safeCount,
                    localPlayerNetId,
                    out Vector3 playerPosition))
            {
                return false;
            }

            if (!IsFiniteNonZero(viewForward))
            {
                return false;
            }

            Vector3 normalizedForward = viewForward.normalized;
            float maximumRangeSquared = maximumRange * maximumRange;
            float bestAlignment = 0f;
            float bestDistanceSquared = float.PositiveInfinity;
            bool found = false;

            for (int index = 0; index < safeCount; ++index)
            {
                RenderEntityState state = states[index];
                if (!IsPickupCandidate(state))
                {
                    continue;
                }

                Vector3 offset = ToVector3(state.position) - playerPosition;
                float distanceSquared = offset.sqrMagnitude;
                if (!float.IsFinite(distanceSquared) ||
                    distanceSquared <= 0.000001f ||
                    distanceSquared > maximumRangeSquared)
                {
                    continue;
                }

                float alignment = Vector3.Dot(
                    normalizedForward,
                    offset / Mathf.Sqrt(distanceSquared));
                if (alignment <= 0f)
                {
                    continue;
                }

                if (!found ||
                    alignment > bestAlignment + 0.0001f ||
                    Mathf.Abs(alignment - bestAlignment) <= 0.0001f &&
                    distanceSquared < bestDistanceSquared)
                {
                    target = state;
                    bestAlignment = alignment;
                    bestDistanceSquared = distanceSquared;
                    found = true;
                }
            }

            return found;
        }

        private static bool TryFindLocalPlayerPosition(
            RenderEntityState[] states,
            int count,
            uint localPlayerNetId,
            out Vector3 position)
        {
            for (int index = 0; index < count; ++index)
            {
                RenderEntityState state = states[index];
                if (state.net_id == localPlayerNetId &&
                    state.entity_type == KernelEntityType.Actor)
                {
                    position = ToVector3(state.position);
                    return IsFinite(position);
                }
            }

            position = default;
            return false;
        }

        private static bool IsPickupCandidate(RenderEntityState state)
        {
            return state.entity_type == KernelEntityType.Prop &&
                state.net_id != 0 &&
                state.template_id != 0 &&
                state.item_instance_id != 0 &&
                (KernelWorldItemMode)state.world_item_mode == KernelWorldItemMode.Placed;
        }

        public static bool IsFiniteNonZero(Vector3 value)
        {
            return IsFinite(value) && value.sqrMagnitude > 0.000001f;
        }

        private static bool IsFinite(Vector3 value)
        {
            return float.IsFinite(value.x) &&
                float.IsFinite(value.y) &&
                float.IsFinite(value.z);
        }

        private static Vector3 ToVector3(KernelVec3 value)
        {
            return new Vector3(value.x, value.y, value.z);
        }
    }
}
