using System.Collections.Generic;
using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    /// <summary>
    /// Where a targeted strike (Meteor Staff, Meteor Storm Staff, Sky Laser)
    /// would land, worked out the way the server does it.
    /// </summary>
    /// <remarks>
    /// Mirrors the kernel's <c>resolve_strike_target</c>. The first ray follows
    /// the aim from the fire point for up to <c>max_range</c> and stops on the
    /// nearest of the world or an actor. Hitting nothing is a refusal, never a
    /// point at the end of the ray. The second ray drops straight down from that
    /// point onto the world only, passing through actors, so aiming at someone
    /// lands at their feet. It starts off the surface the first ray met: 0.1 m
    /// back along a wall's normal, plus 0.05 m up.
    /// <para>
    /// This is a preview only. "The world" here is whatever Unity has colliders
    /// for (the terrain), and actors are approximated as boxes, so the server's
    /// answer can differ slightly. The server's is the one that counts.
    /// </para>
    /// </remarks>
    public static class TargetedStrikeTargeting
    {
        public const float WallBackOff = 0.1f;
        public const float DropLift = 0.05f;

        /// <summary>An actor's hit volume, as a box aligned with the world axes.</summary>
        public readonly struct ActorBox
        {
            public readonly Vector3 Center;
            public readonly Vector3 HalfExtents;

            public ActorBox(Vector3 center, Vector3 halfExtents)
            {
                Center = center;
                HalfExtents = halfExtents;
            }
        }

        /// <summary>Casts against the world; true with the nearest hit within <c>maxDistance</c>.</summary>
        public delegate bool WorldRaycast(
            Vector3 origin,
            Vector3 direction,
            float maxDistance,
            out Vector3 point,
            out Vector3 normal,
            out float distance);

        public static bool TryResolve(
            Vector3 fireOrigin,
            Vector3 aimDirection,
            float maxRange,
            WorldRaycast worldRaycast,
            IReadOnlyList<ActorBox> actors,
            out Vector3 landing)
        {
            landing = default;
            if (worldRaycast == null ||
                !(maxRange > 0f) ||
                aimDirection.sqrMagnitude <= 1e-8f)
            {
                return false;
            }

            Vector3 direction = aimDirection.normalized;
            bool found = false;
            float nearest = maxRange;
            Vector3 point = default;
            Vector3 lift = Vector3.zero;

            if (worldRaycast(
                    fireOrigin,
                    direction,
                    maxRange,
                    out Vector3 worldPoint,
                    out Vector3 worldNormal,
                    out float worldDistance))
            {
                found = true;
                nearest = worldDistance;
                point = worldPoint;
                lift = worldNormal * WallBackOff;
            }

            if (actors != null)
            {
                for (int index = 0; index < actors.Count; ++index)
                {
                    ActorBox actor = actors[index];
                    if (TryRayBox(fireOrigin, direction, actor, out float distance) &&
                        distance < nearest)
                    {
                        found = true;
                        nearest = distance;
                        point = fireOrigin + direction * distance;
                        lift = Vector3.zero;
                    }
                }
            }

            if (!found)
            {
                return false;
            }

            Vector3 dropOrigin = point + lift + new Vector3(0f, DropLift, 0f);
            if (!worldRaycast(
                    dropOrigin,
                    Vector3.down,
                    maxRange,
                    out Vector3 ground,
                    out _,
                    out _))
            {
                return false;
            }

            landing = ground;
            return true;
        }

        /// <summary>
        /// <see cref="WorldRaycast"/> over Unity's physics scene, triggers ignored.
        /// </summary>
        public static WorldRaycast PhysicsRaycast(int layerMask)
        {
            return (Vector3 origin, Vector3 direction, float maxDistance,
                out Vector3 point, out Vector3 normal, out float distance) =>
            {
                if (Physics.Raycast(
                        origin,
                        direction,
                        out RaycastHit hit,
                        maxDistance,
                        layerMask,
                        QueryTriggerInteraction.Ignore))
                {
                    point = hit.point;
                    normal = hit.normal;
                    distance = hit.distance;
                    return true;
                }

                point = default;
                normal = default;
                distance = 0f;
                return false;
            };
        }

        /// <summary>
        /// Where a ray from outside the box first enters it. A ray starting inside
        /// is not counted: the kernel's ray ignores the shooter, and anyone else
        /// standing that close is not what the reticle is on.
        /// </summary>
        private static bool TryRayBox(
            Vector3 origin,
            Vector3 direction,
            ActorBox box,
            out float distance)
        {
            distance = 0f;
            Vector3 min = box.Center - box.HalfExtents;
            Vector3 max = box.Center + box.HalfExtents;
            float enter = 0f;
            float exit = float.PositiveInfinity;
            bool inside = true;
            for (int axis = 0; axis < 3; ++axis)
            {
                float o = origin[axis];
                float d = direction[axis];
                if (o < min[axis] || o > max[axis])
                {
                    inside = false;
                }

                if (Mathf.Abs(d) < 1e-8f)
                {
                    if (o < min[axis] || o > max[axis])
                    {
                        return false;
                    }

                    continue;
                }

                float t1 = (min[axis] - o) / d;
                float t2 = (max[axis] - o) / d;
                if (t1 > t2)
                {
                    (t1, t2) = (t2, t1);
                }

                enter = Mathf.Max(enter, t1);
                exit = Mathf.Min(exit, t2);
                if (enter > exit)
                {
                    return false;
                }
            }

            if (inside)
            {
                return false;
            }

            distance = enter;
            return true;
        }
    }
}
