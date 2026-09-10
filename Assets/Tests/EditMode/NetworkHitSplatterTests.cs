using NetworkExample.UnityDemo.Rendering;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class NetworkHitSplatterTests
    {
        private const float Tolerance = 1e-3f;

        private GameObject gameObject;
        private GameObject rootObject;
        private GameObject ground;

        [SetUp]
        public void SetUp()
        {
            gameObject = new GameObject("NetworkHitSplatterTests");
            rootObject = new GameObject("NetworkHitSplatterTestsRoot");

            // The splatters probe for ground with a Unity raycast, so a scene
            // with no collider is a scene where nothing lands. This is the whole
            // dependency, and it is a real one worth stating in the fixture.
            ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.position = new Vector3(0f, -0.5f, 0f);
            ground.transform.localScale = new Vector3(200f, 1f, 200f);
            Physics.SyncTransforms();
        }

        [TearDown]
        public void TearDown()
        {
            DestroyIfPresent(gameObject);
            DestroyIfPresent(rootObject);
            DestroyIfPresent(ground);
        }

        [Test]
        public void SolvedArc_StartsOnTheOriginAndEndsOnTheLandingPoint()
        {
            var origin = new Vector3(1f, 2f, 3f);
            var landing = new Vector3(5f, 0.5f, -2f);
            var gravity = new Vector3(0f, -9.81f, 0f);
            const float flightSeconds = 1.5f;

            Vector3 launchVelocity = NetworkSplatView.SolveLaunchVelocity(
                origin,
                landing,
                flightSeconds,
                gravity);

            // The whole point of solving backwards: the splat is on its landing
            // point at exactly the moment the decal is switched on, so the decal
            // never has to be placed anywhere the body was not.
            AssertClose(
                NetworkSplatView.SampleArc(origin, launchVelocity, gravity, 0f),
                origin);
            AssertClose(
                NetworkSplatView.SampleArc(origin, launchVelocity, gravity, flightSeconds),
                landing);
        }

        [Test]
        public void SolvedArc_LobsAboveBothOfItsEndpoints()
        {
            var origin = new Vector3(0f, 2f, 0f);
            var landing = new Vector3(4f, 0f, 0f);
            var gravity = new Vector3(0f, -9.81f, 0f);
            const float flightSeconds = 1.5f;

            Vector3 launchVelocity = NetworkSplatView.SolveLaunchVelocity(
                origin,
                landing,
                flightSeconds,
                gravity);
            Vector3 midpoint = NetworkSplatView.SampleArc(
                origin,
                launchVelocity,
                gravity,
                flightSeconds * 0.5f);

            // A splat that travelled in a straight line would read as a slide
            // rather than a throw.
            Assert.That(midpoint.y, Is.GreaterThan(origin.y));
            Assert.That(midpoint.y, Is.GreaterThan(landing.y));
        }

        [Test]
        public void SolvedArc_WithNoFlightTime_IsRefusedRatherThanDividedByZero()
        {
            Assert.That(
                NetworkSplatView.SolveLaunchVelocity(
                    Vector3.zero,
                    Vector3.one,
                    0f,
                    Physics.gravity),
                Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void SurfaceRotation_AimsTheProjectorIntoTheSurface()
        {
            // A URP projector casts down its local +Z, so facing a surface means
            // forward lands on the negated normal -- on level ground and on a
            // slope alike.
            Quaternion level = NetworkSplatView.SurfaceRotation(Vector3.up, 0f);
            AssertClose(level * Vector3.forward, Vector3.down);

            Vector3 slope = new Vector3(0f, 1f, 1f).normalized;
            Quaternion tilted = NetworkSplatView.SurfaceRotation(slope, 0f);
            AssertClose(tilted * Vector3.forward, -slope);
        }

        [Test]
        public void SurfaceRotation_RollsAboutTheProjectionAxisOnly()
        {
            Quaternion unrolled = NetworkSplatView.SurfaceRotation(Vector3.up, 0f);
            Quaternion rolled = NetworkSplatView.SurfaceRotation(Vector3.up, 90f);

            // Roll is what keeps several splats off one texture from reading as
            // copies, so it has to turn the decal without tilting it off the
            // surface it is projecting onto.
            AssertClose(rolled * Vector3.forward, unrolled * Vector3.forward);
            Assert.That(
                Vector3.Angle(rolled * Vector3.up, unrolled * Vector3.up),
                Is.EqualTo(90f).Within(0.01f));
        }

        [Test]
        public void Splat_FliesThenSettlesOnItsLandingPointThenExpires()
        {
            var landing = new Vector3(2f, 0f, 1f);
            NetworkSplatView splat = CreateSplat();
            splat.Play(Launch(Vector3.up, landing, flightSeconds: 1.5f, settledSeconds: 3f));

            Assert.That(splat.CurrentPhase, Is.EqualTo(NetworkSplatView.Phase.Flight));

            splat.Tick(0.5f);
            Assert.That(splat.CurrentPhase, Is.EqualTo(NetworkSplatView.Phase.Flight));
            Assert.That(splat.transform.position, Is.Not.EqualTo(landing));

            splat.Tick(1f);
            Assert.That(splat.CurrentPhase, Is.EqualTo(NetworkSplatView.Phase.Settled));
            AssertClose(splat.transform.position, landing);

            Assert.That(splat.Tick(2.9f), Is.True);
            Assert.That(splat.Tick(0.2f), Is.False);
            Assert.That(splat.CurrentPhase, Is.EqualTo(NetworkSplatView.Phase.Idle));
            Assert.That(splat.IsPlaying, Is.False);
        }

        [Test]
        public void LongFrameAcrossLanding_DoesNotStretchTheSettledLife()
        {
            NetworkSplatView splat = CreateSplat();
            splat.Play(Launch(
                Vector3.up,
                Vector3.zero,
                flightSeconds: 1.5f,
                settledSeconds: 3f));

            // One 2s frame lands the splat and spends 0.5s of its settled life.
            // Dropping that overshoot instead would hand a splat a longer life on
            // a slow machine than on a fast one.
            splat.Tick(2f);
            Assert.That(splat.CurrentPhase, Is.EqualTo(NetworkSplatView.Phase.Settled));

            Assert.That(splat.Tick(2.4f), Is.True);
            Assert.That(splat.Tick(0.2f), Is.False);
        }

        [Test]
        public void OneBurst_ThrowsBetweenThreeAndFiveSplats()
        {
            NetworkHitSplatters splatters = CreateSplatters();

            Assert.That(splatters.TrySplat(Vector3.zero), Is.True);
            Assert.That(splatters.LiveSplatCount, Is.InRange(3, 5));
        }

        [Test]
        public void BurstWithNoGroundUnderneath_LandsNothing()
        {
            NetworkHitSplatters splatters = CreateSplatters();

            // Off the edge of the ground. A splat with nowhere to land is dropped
            // rather than left hanging at the height it was thrown from.
            Assert.That(splatters.TrySplat(new Vector3(5000f, 0f, 5000f)), Is.False);
            Assert.That(splatters.LiveSplatCount, Is.Zero);
        }

        [Test]
        public void ExpiredSplats_AreReusedRatherThanGrowingThePool()
        {
            NetworkHitSplatters splatters = CreateSplatters();

            for (int burst = 0; burst < 6; ++burst)
            {
                splatters.TrySplat(Vector3.zero);
                // Long enough to carry the whole burst through flight, settled
                // life and expiry in one step.
                splatters.Tick(100f);
                Assert.That(splatters.LiveSplatCount, Is.Zero);
            }

            // Six bursts of up to five splats, and never more instances than one
            // burst needs at once.
            Assert.That(splatters.PooledSplatCount, Is.LessThanOrEqualTo(5));
        }

        [Test]
        public void OverlappingBursts_StopAtTheCeilingInsteadOfGrowing()
        {
            NetworkHitSplatters splatters = CreateSplatters();

            // Thirty bursts with nothing ageing between them wants far more than
            // the default ceiling of 64, so the oldest splats are recycled under
            // a load no fight would produce.
            for (int burst = 0; burst < 30; ++burst)
            {
                splatters.TrySplat(Vector3.zero);
            }

            Assert.That(splatters.PooledSplatCount, Is.LessThanOrEqualTo(64));
            Assert.That(splatters.LiveSplatCount, Is.LessThanOrEqualTo(64));
        }

        [Test]
        public void PooledSplats_AreParentedUnderTheConfiguredRoot()
        {
            NetworkHitSplatters splatters = CreateSplatters();

            splatters.TrySplat(Vector3.zero);

            Assert.That(
                rootObject.GetComponentsInChildren<NetworkSplatView>(true).Length,
                Is.InRange(3, 5));
        }

        private NetworkHitSplatters CreateSplatters()
        {
            NetworkHitSplatters splatters =
                gameObject.AddComponent<NetworkHitSplatters>();
            splatters.Configure(rootObject.transform);
            return splatters;
        }

        private NetworkSplatView CreateSplat()
        {
            var splatObject = new GameObject("Splat");
            splatObject.transform.SetParent(rootObject.transform);
            return splatObject.AddComponent<NetworkSplatView>();
        }

        private static NetworkSplatLaunch Launch(
            Vector3 origin,
            Vector3 landing,
            float flightSeconds,
            float settledSeconds)
        {
            return new NetworkSplatLaunch
            {
                origin = origin,
                landing = landing,
                landingNormal = Vector3.up,
                gravity = Physics.gravity,
                flightSeconds = flightSeconds,
                settledSeconds = settledSeconds,
                fadeSeconds = 0.5f,
                decalRollDegrees = 0f,
                decalSize = Vector3.one,
                spinDegreesPerSecond = Vector3.zero,
            };
        }

        private static void AssertClose(Vector3 actual, Vector3 expected)
        {
            Assert.That(
                Vector3.Distance(actual, expected),
                Is.LessThan(Tolerance),
                $"expected {expected} but was {actual}");
        }

        private static void DestroyIfPresent(GameObject target)
        {
            if (target != null)
            {
                Object.DestroyImmediate(target);
            }
        }
    }
}
