using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    /// <summary>
    /// Everything one splat needs to know at the moment it is thrown.
    /// </summary>
    /// <remarks>
    /// Gathered into a struct rather than passed as a dozen arguments because
    /// <see cref="NetworkHitSplatters"/> rolls most of these per splat -- the
    /// scatter, the roll, the size, the settled life are all randomized -- and a
    /// test that wants one specific arc needs to be able to state that arc
    /// exactly, without the randomness in the way.
    /// </remarks>
    public struct NetworkSplatLaunch
    {
        /// <summary>Where the splat is thrown from.</summary>
        public Vector3 origin;

        /// <summary>The probed point the arc has to arrive on.</summary>
        public Vector3 landing;

        /// <summary>Outward normal of the surface at <see cref="landing"/>.</summary>
        public Vector3 landingNormal;

        /// <summary>Acceleration applied over the arc.</summary>
        public Vector3 gravity;

        /// <summary>Seconds from <see cref="origin"/> to <see cref="landing"/>.</summary>
        public float flightSeconds;

        /// <summary>Seconds the decal stays once it has landed.</summary>
        public float settledSeconds;

        /// <summary>
        /// Seconds of fade at the end of the settled life. Clamped to
        /// <see cref="settledSeconds"/>.
        /// </summary>
        public float fadeSeconds;

        /// <summary>Roll about the projection axis, so splats are not clones.</summary>
        public float decalRollDegrees;

        /// <summary>
        /// Projector box. X and Y are the footprint on the surface; Z is how far
        /// the projection reaches through it.
        /// </summary>
        public Vector3 decalSize;

        /// <summary>Tumble applied to the body while it is airborne.</summary>
        public Vector3 spinDegreesPerSecond;
    }
}
