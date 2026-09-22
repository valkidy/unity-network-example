using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.LocalAgent
{
    /// <summary>
    /// Does a sight line (a segment from a to b) pass through a solid shape?
    /// An endpoint inside a shape counts as passing through it.
    /// </summary>
    public static class LineOfSightGeometry
    {
        private const float Epsilon = 1e-6f;

        public static bool SegmentHitsSphere(Vector3 a, Vector3 b, Vector3 center, float radius)
        {
            if (!(radius > 0f)) return false;
            Vector3 d = b - a;
            float lengthSq = d.sqrMagnitude;
            float t = lengthSq > Epsilon ? Mathf.Clamp01(Vector3.Dot(center - a, d) / lengthSq) : 0f;
            return (a + d * t - center).sqrMagnitude <= radius * radius;
        }

        /// <summary>Slab test against a box aligned with the world axes.</summary>
        public static bool SegmentHitsAabb(Vector3 a, Vector3 b, Vector3 center, Vector3 halfExtents)
        {
            if (!(halfExtents.x > 0f && halfExtents.y > 0f && halfExtents.z > 0f)) return false;
            Vector3 d = b - a;
            float enter = 0f, exit = 1f;
            for (int axis = 0; axis < 3; axis++)
            {
                float min = center[axis] - halfExtents[axis], max = center[axis] + halfExtents[axis];
                if (Mathf.Abs(d[axis]) < Epsilon)
                {
                    if (a[axis] < min || a[axis] > max) return false;
                    continue;
                }
                float t1 = (min - a[axis]) / d[axis], t2 = (max - a[axis]) / d[axis];
                if (t1 > t2) (t1, t2) = (t2, t1);
                enter = Mathf.Max(enter, t1);
                exit = Mathf.Min(exit, t2);
                if (enter > exit) return false;
            }
            return true;
        }

        public static bool SegmentHitsOrientedBox(Vector3 a, Vector3 b, Vector3 center,
            Vector3 halfExtents, Quaternion rotation)
        {
            Quaternion inverse = Quaternion.Inverse(Normalized(rotation));
            return SegmentHitsAabb(inverse * (a - center), inverse * (b - center), Vector3.zero, halfExtents);
        }

        public static bool SegmentHitsCapsule(Vector3 a, Vector3 b, Vector3 start, Vector3 end, float radius)
        {
            if (!(radius > 0f)) return false;
            return SegmentDistanceSq(a, b, start, end) <= radius * radius;
        }

        /// <summary>
        /// Whether the shape blocks the segment. Cones are vision and trigger
        /// volumes, not solids, and never block; neither does a degenerate or
        /// non-finite shape.
        /// </summary>
        public static bool SegmentHitsShape(Vector3 a, Vector3 b, in KernelColliderShapeView shape)
        {
            Vector3 center = ToVector3(shape.world_center);
            Vector3 extents = new Vector3(shape.shape_params.x, shape.shape_params.y, shape.shape_params.z);
            switch ((KernelColliderShapeType)shape.shape_type)
            {
                case KernelColliderShapeType.Sphere:
                    return IsFinite(center) && SegmentHitsSphere(a, b, center, shape.shape_params.x);
                case KernelColliderShapeType.Aabb:
                    return IsFinite(center) && IsFinite(extents) && SegmentHitsAabb(a, b, center, extents);
                case KernelColliderShapeType.OrientedBox:
                    return IsFinite(center) && IsFinite(extents) &&
                        SegmentHitsOrientedBox(a, b, center, extents, ToQuaternion(shape.world_rotation));
                case KernelColliderShapeType.Segment:
                    // Endpoints are explicit; shape_params.x is the length, .y the radius.
                    Vector3 start = ToVector3(shape.segment_start), end = ToVector3(shape.segment_end);
                    return IsFinite(start) && IsFinite(end) &&
                        SegmentHitsCapsule(a, b, start, end, shape.shape_params.y);
                default:
                    return false;
            }
        }

        /// <summary>Squared distance between segments p1-q1 and p2-q2 (Ericson, Real-Time Collision Detection 5.1.9).</summary>
        public static float SegmentDistanceSq(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = d1.sqrMagnitude, e = d2.sqrMagnitude, f = Vector3.Dot(d2, r);
            float s, t;
            if (a <= Epsilon && e <= Epsilon) return r.sqrMagnitude;
            if (a <= Epsilon)
            {
                s = 0f;
                t = Mathf.Clamp01(f / e);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= Epsilon)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float bDot = Vector3.Dot(d1, d2);
                    float denom = a * e - bDot * bDot;
                    s = denom > Epsilon ? Mathf.Clamp01((bDot * f - c * e) / denom) : 0f;
                    t = (bDot * s + f) / e;
                    if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                    else if (t > 1f) { t = 1f; s = Mathf.Clamp01((bDot - c) / a); }
                }
            }
            return (p1 + d1 * s - (p2 + d2 * t)).sqrMagnitude;
        }

        // An all-zero rotation is what an unset kernel quaternion looks like; read it as identity.
        private static Quaternion ToQuaternion(KernelQuat value) =>
            value.x == 0f && value.y == 0f && value.z == 0f && value.w == 0f
                ? Quaternion.identity : new Quaternion(value.x, value.y, value.z, value.w);
        private static Quaternion Normalized(Quaternion q)
        {
            float length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return length > Epsilon && float.IsFinite(length)
                ? new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length) : Quaternion.identity;
        }
        private static Vector3 ToVector3(KernelVec3 value) => new Vector3(value.x, value.y, value.z);
        private static bool IsFinite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    }
}
