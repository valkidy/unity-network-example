using UnityEngine;

namespace NetworkExample.UnityDemo.CameraSystem
{
    /// <summary>
    /// Turns the camera's reticle ray into the direction a shot has to leave the
    /// character along to reach whatever the reticle is on.
    /// </summary>
    /// <remarks>
    /// The server fires from the character (its position plus one metre up), not
    /// from the camera. Sending the camera's own direction makes the two rays
    /// parallel, so the shot lands wherever the offset between them carries it:
    /// with the camera a metre above the fire point and aiming 10 degrees down,
    /// about 5.7 m short of the reticle. Aiming from the fire point at the point
    /// the reticle ray hits closes that gap at every range.
    /// </remarks>
    public static class ReticleAimConvergence
    {
        /// <summary>How far away the reticle is taken to point when it hits nothing.</summary>
        public const float DefaultFarDistance = 1000f;

        /// <summary>
        /// The point the reticle is on: the first collider the ray meets, or a
        /// point <paramref name="farDistance"/> along it when it meets nothing.
        /// </summary>
        /// <remarks>
        /// The query starts level with <paramref name="fireOrigin"/> rather than at
        /// the camera, so ground and scenery between the camera and the character
        /// -- which the shot can never reach -- are not mistaken for the target.
        /// </remarks>
        public static Vector3 TargetPoint(
            Ray reticleRay,
            Vector3 fireOrigin,
            int layerMask,
            float farDistance = DefaultFarDistance)
        {
            Vector3 direction = reticleRay.direction.normalized;
            float skip = Mathf.Max(0f, Vector3.Dot(fireOrigin - reticleRay.origin, direction));
            Vector3 start = reticleRay.origin + direction * skip;
            float reach = Mathf.Max(0f, farDistance - skip);
            if (reach > 0f &&
                Physics.Raycast(
                    start,
                    direction,
                    out RaycastHit hit,
                    reach,
                    layerMask,
                    QueryTriggerInteraction.Ignore))
            {
                return hit.point;
            }

            return reticleRay.origin + direction * farDistance;
        }

        /// <summary>
        /// The direction from <paramref name="fireOrigin"/> to
        /// <paramref name="targetPoint"/>, or <paramref name="fallback"/> when the
        /// two are too close to give one.
        /// </summary>
        public static Vector3 DirectionTo(Vector3 fireOrigin, Vector3 targetPoint, Vector3 fallback)
        {
            Vector3 toTarget = targetPoint - fireOrigin;
            if (toTarget.sqrMagnitude > 0.0001f)
            {
                return toTarget.normalized;
            }

            return fallback.sqrMagnitude > 0.000001f ? fallback.normalized : Vector3.forward;
        }
    }
}
