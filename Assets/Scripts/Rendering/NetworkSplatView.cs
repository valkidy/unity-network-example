using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace NetworkExample.UnityDemo.Rendering
{
    /// <summary>
    /// One splatter: a body thrown along an arc, and the decal it leaves where
    /// that arc ends.
    /// </summary>
    /// <remarks>
    /// The arc is solved backwards from the landing point rather than integrated
    /// forwards from a launch impulse. A splat has to know where it lands before
    /// it is thrown -- the ground probe that finds that point is the expensive
    /// half of the effect, and doing it once at spawn is what keeps a death from
    /// costing a physics query per splat per frame. Given the two endpoints and a
    /// flight time the launch velocity falls out in closed form, so the body is
    /// guaranteed to arrive exactly on the probed point at exactly the right
    /// moment, which is what the decal's placement depends on.
    ///
    /// A decal draws nothing until its projection box meets a surface, so the
    /// airborne half cannot be the decal itself: the body is an ordinary mesh
    /// while it flies, and the projector is switched on only once it has landed.
    ///
    /// Like <see cref="NetworkTracerView"/> this is pure presentation with no
    /// kernel entity behind it, so <see cref="Tick"/> is driven by the pool that
    /// owns it rather than by Unity. That is what makes the whole lifetime
    /// exercisable from an EditMode test, where no Update runs.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class NetworkSplatView : MonoBehaviour
    {
        /// <summary>
        /// Where a splat is in its life. A pooled instance sits in
        /// <see cref="Phase.Idle"/> and is handed back out from there.
        /// </summary>
        public enum Phase
        {
            Idle = 0,
            Flight = 1,
            Settled = 2,
        }

        [SerializeField]
        [Tooltip(
            "The mesh shown while the splat is in the air. Left empty, the first " +
            "child is used. Hidden once the splat lands and the decal takes over.")]
        private Transform flightBody;

        [SerializeField]
        [Tooltip(
            "Projects the splat onto whatever it landed on. Left empty, the " +
            "projector on this object or its children is used; with no projector " +
            "at all the splat still flies and expires, it just leaves no mark.")]
        private DecalProjector decal;

        private Vector3 launchPosition;
        private Vector3 launchVelocity;
        private Vector3 landingPosition;
        private Quaternion landingPose = Quaternion.identity;
        private Vector3 decalSize = Vector3.one;
        private Vector3 gravity = Physics.gravity;
        private Quaternion spinPerSecond = Quaternion.identity;

        private float flightSeconds;
        private float flightElapsed;
        private float settledSeconds;
        private float settledElapsed;
        private float fadeSeconds;
        private float appliedFade = -1f;

        private Phase phase = Phase.Idle;

        public Phase CurrentPhase => phase;

        public bool IsPlaying => phase != Phase.Idle;

        /// <summary>
        /// Names the two children the pool built, so neither is searched for nor
        /// guessed at from child order.
        /// </summary>
        public void Bind(Transform body, DecalProjector projector)
        {
            flightBody = body;
            decal = projector;
        }

        /// <summary>
        /// The launch velocity that carries a body from <paramref name="origin"/>
        /// to <paramref name="landing"/> in exactly
        /// <paramref name="flightSeconds"/> under <paramref name="gravity"/>.
        /// </summary>
        /// <remarks>
        /// From p(t) = p0 + v0*t + 0.5*g*t^2, substituting p(T) = p1 and solving
        /// for v0 gives v0 = (p1 - p0)/T - 0.5*g*T. There is exactly one such
        /// velocity for a given flight time, so the arc's shape is chosen by
        /// picking T: a longer one lobs higher over the same two points.
        /// </remarks>
        public static Vector3 SolveLaunchVelocity(
            Vector3 origin,
            Vector3 landing,
            float flightSeconds,
            Vector3 gravity)
        {
            if (flightSeconds <= 0f)
            {
                return Vector3.zero;
            }

            return ((landing - origin) / flightSeconds) - (0.5f * flightSeconds * gravity);
        }

        /// <summary>
        /// The point on the arc at <paramref name="seconds"/> after launch.
        /// </summary>
        public static Vector3 SampleArc(
            Vector3 origin,
            Vector3 launchVelocity,
            Vector3 gravity,
            float seconds)
        {
            return origin +
                (launchVelocity * seconds) +
                (0.5f * seconds * seconds * gravity);
        }

        /// <summary>
        /// The rotation that aims a decal projector at a surface whose outward
        /// normal is <paramref name="surfaceNormal"/>, rolled by
        /// <paramref name="rollDegrees"/> about the projection axis.
        /// </summary>
        /// <remarks>
        /// A URP projector casts along its local +Z, so facing a surface means
        /// pointing forward into it -- the negated normal. The roll is what keeps
        /// several splats off one texture from reading as copies of each other.
        /// </remarks>
        public static Quaternion SurfaceRotation(Vector3 surfaceNormal, float rollDegrees)
        {
            Vector3 normal = surfaceNormal.sqrMagnitude > 1e-8f
                ? surfaceNormal.normalized
                : Vector3.up;
            Vector3 forward = -normal;
            // LookRotation needs an up that is not the axis it is looking down,
            // and on level ground the obvious choice is exactly that axis.
            Vector3 upHint = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.99f
                ? Vector3.forward
                : Vector3.up;
            return Quaternion.LookRotation(forward, upHint) *
                Quaternion.Euler(0f, 0f, rollDegrees);
        }

        /// <summary>
        /// Throws this splat from <paramref name="origin"/> onto the surface
        /// described by <paramref name="landing"/> and
        /// <paramref name="landingNormal"/>.
        /// </summary>
        public void Play(in NetworkSplatLaunch launch)
        {
            if (launch.flightSeconds <= 0f || launch.settledSeconds <= 0f)
            {
                Stop();
                return;
            }

            launchPosition = launch.origin;
            gravity = launch.gravity;
            launchVelocity = SolveLaunchVelocity(
                launch.origin,
                launch.landing,
                launch.flightSeconds,
                launch.gravity);
            flightSeconds = launch.flightSeconds;
            flightElapsed = 0f;
            settledSeconds = launch.settledSeconds;
            settledElapsed = 0f;
            // A fade longer than the splat's whole settled life would have it
            // start already half gone.
            fadeSeconds = Mathf.Clamp(launch.fadeSeconds, 0f, launch.settledSeconds);
            spinPerSecond = Quaternion.Euler(launch.spinDegreesPerSecond);

            landingPose = SurfaceRotation(launch.landingNormal, launch.decalRollDegrees);
            landingPosition = launch.landing;
            decalSize = launch.decalSize;

            phase = Phase.Flight;
            // Posed before it is shown: OnEnable on a re-used splat -- and on a
            // freshly built one, which wakes up at the origin -- would otherwise
            // run while the object still stands wherever it last was, which is
            // where any world-space effect on the body starts emitting.
            transform.SetPositionAndRotation(launch.origin, Quaternion.identity);
            gameObject.SetActive(true);
            ShowFlightBody(true);
            ShowDecal(false);
            appliedFade = -1f;
        }

        /// <summary>
        /// Ages the splat by one frame. Returns false once it has expired, which
        /// is the pool's signal to take it back.
        /// </summary>
        public bool Tick(float deltaSeconds)
        {
            if (phase == Phase.Idle)
            {
                return false;
            }

            float delta = Mathf.Max(0f, deltaSeconds);
            if (phase == Phase.Flight)
            {
                flightElapsed += delta;
                if (flightElapsed < flightSeconds)
                {
                    transform.SetPositionAndRotation(
                        SampleArc(launchPosition, launchVelocity, gravity, flightElapsed),
                        Quaternion.SlerpUnclamped(
                            Quaternion.identity,
                            spinPerSecond,
                            flightElapsed));
                    return true;
                }

                // Overshoot is carried into the settled phase rather than
                // dropped, so a long frame does not stretch the splat's life.
                delta = flightElapsed - flightSeconds;
                Settle();
            }

            settledElapsed += delta;
            if (settledElapsed >= settledSeconds)
            {
                Stop();
                return false;
            }

            ApplyFade(RemainingFade());
            return true;
        }

        public void Stop()
        {
            phase = Phase.Idle;
            flightSeconds = 0f;
            flightElapsed = 0f;
            settledSeconds = 0f;
            settledElapsed = 0f;
            appliedFade = -1f;
            ShowDecal(false);
            ShowFlightBody(false);
            gameObject.SetActive(false);
        }

        private void Settle()
        {
            phase = Phase.Settled;
            transform.SetPositionAndRotation(landingPosition, landingPose);
            ShowFlightBody(false);
            ShowDecal(true);
            ApplyFade(1f);
        }

        private float RemainingFade()
        {
            if (fadeSeconds <= 0f)
            {
                return 1f;
            }

            float remaining = settledSeconds - settledElapsed;
            return remaining >= fadeSeconds ? 1f : Mathf.Clamp01(remaining / fadeSeconds);
        }

        private void ApplyFade(float fade)
        {
            // DecalProjector.fadeFactor runs OnValidate on every assignment, so
            // it is written only when the value has actually moved.
            if (decal == null || Mathf.Abs(fade - appliedFade) < 0.001f)
            {
                return;
            }

            appliedFade = fade;
            decal.fadeFactor = fade;
        }

        private void ShowDecal(bool visible)
        {
            DecalProjector projector = ResolveDecal();
            if (projector == null)
            {
                return;
            }

            if (visible)
            {
                projector.size = decalSize;
                projector.pivot = Vector3.zero;
            }

            // Enabling is what registers the projector with URP's decal entity
            // manager, so a pooled splat costs nothing while it waits.
            projector.enabled = visible;
        }

        private void ShowFlightBody(bool visible)
        {
            Transform body = ResolveFlightBody();
            if (body != null)
            {
                body.gameObject.SetActive(visible);
            }
        }

        private DecalProjector ResolveDecal()
        {
            if (decal == null)
            {
                decal = GetComponentInChildren<DecalProjector>(true);
            }

            return decal;
        }

        private Transform ResolveFlightBody()
        {
            if (flightBody != null)
            {
                return flightBody;
            }

            return transform.childCount > 0 ? transform.GetChild(0) : null;
        }
    }
}
