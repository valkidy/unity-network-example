using NetworkExample.UnityDemo.Rendering;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class NetworkActorFacingTests
    {
        private GameObject actorObject;
        private NetworkActorView view;

        [SetUp]
        public void SetUp()
        {
            actorObject = new GameObject("NetworkActorFacingTests");
            view = actorObject.AddComponent<NetworkActorView>();
        }

        [TearDown]
        public void TearDown()
        {
            if (actorObject != null)
            {
                Object.DestroyImmediate(actorObject);
            }
        }

        [Test]
        public void ResolveFacing_WhenNotAimDriven_FollowsVelocity()
        {
            bool resolved = NetworkActorView.TryResolveFacingForward(
                Vector3.right,
                new Vector3(0f, 0f, -5f),
                aimDriven: false,
                out Vector3 forward);

            Assert.That(resolved, Is.True);
            AssertDirection(forward, Vector3.back);
        }

        /// <summary>
        /// This is the backpedal case. The move vector is already camera-relative,
        /// so walking away from the aim gives a velocity pointing behind the
        /// character; facing has to stay on the aim or the character turns round
        /// and runs at the camera instead of stepping backwards.
        /// </summary>
        [Test]
        public void ResolveFacing_WhenAimDrivenAndMovingBackwards_KeepsFacingTheAim()
        {
            bool resolved = NetworkActorView.TryResolveFacingForward(
                Vector3.forward,
                new Vector3(0f, 0f, -5f),
                aimDriven: true,
                out Vector3 forward);

            Assert.That(resolved, Is.True);
            AssertDirection(forward, Vector3.forward);
        }

        [Test]
        public void ResolveFacing_FlattensThePitchOutOfTheAim()
        {
            bool resolved = NetworkActorView.TryResolveFacingForward(
                new Vector3(0f, 0.9f, 1f).normalized,
                Vector3.zero,
                aimDriven: true,
                out Vector3 forward);

            Assert.That(resolved, Is.True);
            Assert.That(forward.y, Is.EqualTo(0f).Within(0.0001f));
            AssertDirection(forward, Vector3.forward);
        }

        [Test]
        public void ResolveFacing_WhenAimDrivenWithNoAim_FallsBackToVelocity()
        {
            bool resolved = NetworkActorView.TryResolveFacingForward(
                Vector3.zero,
                new Vector3(5f, 0f, 0f),
                aimDriven: true,
                out Vector3 forward);

            Assert.That(resolved, Is.True);
            AssertDirection(forward, Vector3.right);
        }

        [Test]
        public void ResolveFacing_StandingStillAndNotAiming_ResolvesNothing()
        {
            bool resolved = NetworkActorView.TryResolveFacingForward(
                Vector3.zero,
                Vector3.zero,
                aimDriven: false,
                out _);

            Assert.That(resolved, Is.False);
        }

        [Test]
        public void ApplyFacing_StandingStillAndNotAiming_HoldsThePreviousFacing()
        {
            view.ApplyFacing(Vector3.zero, new Vector3(5f, 0f, 0f), false, 1f);
            Quaternion moving = actorObject.transform.rotation;

            view.ApplyFacing(Vector3.zero, Vector3.zero, false, 1f);

            Assert.That(
                Quaternion.Angle(actorObject.transform.rotation, moving),
                Is.LessThan(0.001f));
        }

        /// <summary>
        /// Velocity turns are rate limited too. If they were not, the frame an
        /// action ends -- when facing hands back from the aim to the velocity --
        /// would snap the body round, so firing once while running would swing onto
        /// the reticle and then flick back to the run direction.
        /// </summary>
        [Test]
        public void ApplyFacing_VelocityDriven_IsRateLimitedLikeTheAim()
        {
            view.ApplyFacing(Vector3.zero, new Vector3(0f, 0f, 1f), false, 1f);

            view.ApplyFacing(Vector3.zero, new Vector3(0f, 0f, -1f), false, 1f / 60f);

            float turned = Quaternion.Angle(
                actorObject.transform.rotation, Quaternion.identity);
            Assert.That(turned, Is.EqualTo(9f).Within(0.01f));
        }

        [Test]
        public void ApplyFacing_HandingBackFromAimToVelocity_DoesNotSnap()
        {
            view.ApplyFacing(Vector3.forward, Vector3.zero, true, 1f);

            // The action ends and facing returns to a velocity pointing the other
            // way; the body must ease round rather than flick.
            view.ApplyFacing(Vector3.zero, new Vector3(0f, 0f, -1f), false, 1f / 60f);

            float turned = Quaternion.Angle(
                actorObject.transform.rotation, Quaternion.identity);
            Assert.That(turned, Is.EqualTo(9f).Within(0.01f));
        }

        /// <summary>
        /// A shot fired from a standstill must not teleport the body round to face
        /// it. At the default 540 deg/s a single 1/60s step can only cover 9
        /// degrees of a 180 degree turn.
        /// </summary>
        [Test]
        public void ApplyFacing_AimDriven_TurnsNoFasterThanTheConfiguredRate()
        {
            view.ApplyFacing(Vector3.zero, new Vector3(0f, 0f, 1f), false, 1f);
            Assert.That(
                Quaternion.Angle(actorObject.transform.rotation, Quaternion.identity),
                Is.LessThan(0.001f));

            view.ApplyFacing(Vector3.back, Vector3.zero, true, 1f / 60f);

            float turned = Quaternion.Angle(
                actorObject.transform.rotation, Quaternion.identity);
            Assert.That(turned, Is.EqualTo(9f).Within(0.01f));
        }

        [Test]
        public void ApplyFacing_AimDriven_EventuallyReachesTheAim()
        {
            view.ApplyFacing(Vector3.zero, new Vector3(0f, 0f, 1f), false, 1f);

            for (int step = 0; step < 60; ++step)
            {
                view.ApplyFacing(Vector3.back, Vector3.zero, true, 1f / 60f);
            }

            AssertDirection(actorObject.transform.forward, Vector3.back);
        }

        /// <summary>
        /// The very first facing an actor resolves has nothing to turn away from,
        /// so it has to land on the aim immediately rather than crawl there from
        /// whatever rotation the spawn happened to use.
        /// </summary>
        [Test]
        public void ApplyFacing_OnTheFirstResolvedFacing_AdoptsTheAimImmediately()
        {
            view.ApplyFacing(Vector3.right, Vector3.zero, true, 1f / 60f);

            AssertDirection(actorObject.transform.forward, Vector3.right);
        }

        private static void AssertDirection(Vector3 actual, Vector3 expected)
        {
            Assert.That(Vector3.Angle(actual, expected), Is.LessThan(0.01f));
        }
    }
}
