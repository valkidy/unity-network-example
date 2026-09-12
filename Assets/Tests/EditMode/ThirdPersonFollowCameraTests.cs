using System.Reflection;
using NetworkExample.UnityDemo.CameraSystem;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.LowLevel;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class ThirdPersonFollowCameraTests
    {
        private GameObject cameraObject;
        private GameObject targetObject;
        private GameObject secondTargetObject;
        private ThirdPersonFollowCamera followCamera;
        private Gamepad gamepad;
        private DualSenseGamepadHID dualSense;

        [SetUp]
        public void SetUp()
        {
            cameraObject = new GameObject("ThirdPersonFollowCameraTests");
            followCamera = cameraObject.AddComponent<ThirdPersonFollowCamera>();
            targetObject = new GameObject("FollowTarget");
        }

        [TearDown]
        public void TearDown()
        {
            if (gamepad != null && gamepad.added)
            {
                InputSystem.RemoveDevice(gamepad);
            }

            if (dualSense != null && dualSense.added)
            {
                InputSystem.RemoveDevice(dualSense);
            }

            if (secondTargetObject != null)
            {
                Object.DestroyImmediate(secondTargetObject);
            }

            if (targetObject != null)
            {
                Object.DestroyImmediate(targetObject);
            }

            if (cameraObject != null)
            {
                Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void Defaults_CreateExpectedExplorationFraming()
        {
            targetObject.transform.position = new Vector3(3f, 2f, -4f);

            followCamera.SetTarget(targetObject.transform);

            Quaternion expectedRotation = Quaternion.Euler(12f, 0f, 0f);
            Vector3 expectedShoulderPivot =
                targetObject.transform.position + new Vector3(0.45f, 2.15f, 0f);
            Vector3 expectedPosition =
                expectedShoulderPivot - expectedRotation * Vector3.forward * 3.7f;
            Camera controlledCamera = cameraObject.GetComponent<Camera>();

            AssertVectorApproximately(cameraObject.transform.position, expectedPosition);
            Assert.That(
                Quaternion.Angle(cameraObject.transform.rotation, expectedRotation),
                Is.LessThan(0.001f));
            Assert.That(controlledCamera.fieldOfView, Is.EqualTo(65f).Within(0.0001f));
            Assert.That(controlledCamera.nearClipPlane, Is.EqualTo(0.1f).Within(0.0001f));
        }

        [Test]
        public void SnapFraming_WhileAiming_UsesAimProfileFraming()
        {
            targetObject.transform.position = new Vector3(3f, 2f, -4f);
            followCamera.SetTarget(targetObject.transform);

            followCamera.SetAiming(true);
            followCamera.SnapFraming();

            Quaternion expectedRotation = Quaternion.Euler(12f, 0f, 0f);
            Vector3 expectedShoulderPivot =
                targetObject.transform.position + new Vector3(0.65f, 1.95f, 0f);
            Vector3 expectedPosition =
                expectedShoulderPivot - expectedRotation * Vector3.forward * 3.4f;
            Camera controlledCamera = cameraObject.GetComponent<Camera>();

            Assert.That(followCamera.AimBlend, Is.EqualTo(1f).Within(0.0001f));
            AssertVectorApproximately(cameraObject.transform.position, expectedPosition);
            Assert.That(controlledCamera.fieldOfView, Is.EqualTo(47f).Within(0.0001f));
        }

        /// <summary>
        /// Entering the aim state is a blend, not a cut. Half a blend duration in,
        /// every framing value -- and the camera's own field of view -- has to sit
        /// halfway between the two profiles rather than having already arrived.
        /// </summary>
        [Test]
        public void UpdateFraming_HalfwayThroughBlend_InterpolatesBothProfiles()
        {
            followCamera.SetAiming(true);

            followCamera.UpdateFraming(0.09f);

            Camera controlledCamera = cameraObject.GetComponent<Camera>();
            CameraFramingProfile framing = followCamera.CurrentFraming;

            Assert.That(followCamera.AimBlend, Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(framing.followDistance, Is.EqualTo(3.55f).Within(0.0001f));
            Assert.That(framing.pivotHeight, Is.EqualTo(2.05f).Within(0.0001f));
            Assert.That(framing.shoulderOffset, Is.EqualTo(0.55f).Within(0.0001f));
            Assert.That(controlledCamera.fieldOfView, Is.EqualTo(56f).Within(0.0001f));
        }

        [Test]
        public void UpdateFraming_WhenAimingIsReleased_ReturnsToHipFraming()
        {
            followCamera.SetAiming(true);
            followCamera.SnapFraming();

            followCamera.SetAiming(false);
            followCamera.UpdateFraming(1f);

            Camera controlledCamera = cameraObject.GetComponent<Camera>();

            Assert.That(followCamera.AimBlend, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(controlledCamera.fieldOfView, Is.EqualTo(65f).Within(0.0001f));
        }

        [Test]
        public void ApplyOrbitInput_UsesConfiguredSpeedsAndClampsPitch()
        {
            followCamera.ApplyOrbitInput(Vector2.right, 1f);

            Assert.That(followCamera.CurrentYaw, Is.EqualTo(90f).Within(0.0001f));

            followCamera.ApplyOrbitInput(Vector2.up * 10f, 1f);
            Assert.That(followCamera.CurrentPitch, Is.EqualTo(-10f).Within(0.0001f));

            followCamera.ApplyOrbitInput(Vector2.down * 10f, 1f);
            Assert.That(followCamera.CurrentPitch, Is.EqualTo(55f).Within(0.0001f));
        }

        /// <summary>
        /// The aim profile narrows the lens, so holding the hip orbit speed would
        /// sweep the reticle across far more of the frame per second than it does
        /// while hip-framed. The speeds have to fall with the blend.
        /// </summary>
        [Test]
        public void ApplyOrbitInput_WhileFullyAimed_UsesSlowerOrbitSpeeds()
        {
            followCamera.SetAiming(true);
            followCamera.SnapFraming();

            followCamera.ApplyOrbitInput(Vector2.right, 1f);
            Assert.That(followCamera.CurrentYaw, Is.EqualTo(55f).Within(0.0001f));

            followCamera.ApplyOrbitInput(Vector2.down, 1f);
            Assert.That(followCamera.CurrentPitch, Is.EqualTo(12f + 35f).Within(0.0001f));
        }

        [Test]
        public void ApplyOrbitInput_HalfwayIntoTheBlend_UsesHalfwayOrbitSpeeds()
        {
            followCamera.SetAiming(true);
            followCamera.UpdateFraming(0.09f);

            followCamera.ApplyOrbitInput(Vector2.right, 1f);

            Assert.That(followCamera.CurrentYaw, Is.EqualTo((90f + 55f) * 0.5f).Within(0.0001f));
        }

        [Test]
        public void ReticleViewportPoint_BlendsFromCentreToTheAimOffset()
        {
            AssertVector2Approximately(
                followCamera.CurrentReticleViewportPoint, new Vector2(0.5f, 0.5f));

            followCamera.SetAiming(true);
            followCamera.UpdateFraming(0.09f);
            AssertVector2Approximately(
                followCamera.CurrentReticleViewportPoint, new Vector2(0.565f, 0.5f));

            followCamera.SnapFraming();
            AssertVector2Approximately(
                followCamera.CurrentReticleViewportPoint, new Vector2(0.63f, 0.5f));
        }

        [Test]
        public void AimDirection_WhileHipFramed_MatchesCameraForward()
        {
            cameraObject.transform.rotation = Quaternion.Euler(-18f, 42f, 0f);

            Vector3 aim = followCamera.AimDirection;

            Assert.That(
                Vector3.Angle(aim, cameraObject.transform.forward),
                Is.LessThan(0.01f));
        }

        /// <summary>
        /// Once the reticle sits off centre the aim has to leave camera forward and
        /// follow it, or the reticle is drawing a lie. At the reference offset and
        /// the aim lens that works out to about 11.4 degrees of yaw.
        /// </summary>
        [Test]
        public void AimDirection_WhileAimed_YawsTowardTheReticlePoint()
        {
            Camera controlledCamera = cameraObject.GetComponent<Camera>();
            controlledCamera.aspect = 16f / 9f;
            cameraObject.transform.rotation = Quaternion.identity;
            followCamera.SetAiming(true);
            followCamera.SnapFraming();

            Vector3 aim = followCamera.AimDirection;
            float yaw = Vector3.SignedAngle(Vector3.forward, aim, Vector3.up);

            Assert.That(yaw, Is.EqualTo(11.37f).Within(0.1f));
            Assert.That(aim.y, Is.EqualTo(0f).Within(0.0001f));
        }

        [Test]
        public void SetTarget_WhenTargetChanges_SnapsWithoutUsingPreviousPose()
        {
            targetObject.transform.position = new Vector3(1f, 0f, 2f);
            followCamera.SetTarget(targetObject.transform);
            Vector3 firstPose = cameraObject.transform.position;

            followCamera.SetTarget(null);
            AssertVectorApproximately(cameraObject.transform.position, firstPose);

            secondTargetObject = new GameObject("SecondFollowTarget");
            secondTargetObject.transform.position = new Vector3(20f, 3f, -10f);
            followCamera.SetTarget(secondTargetObject.transform);
            Pose expected = followCamera.CalculateDesiredPose(
                secondTargetObject.transform.position);

            Assert.That(followCamera.FollowTarget, Is.EqualTo(secondTargetObject.transform));
            AssertVectorApproximately(cameraObject.transform.position, expected.position);
            Assert.That(Vector3.Distance(firstPose, expected.position), Is.GreaterThan(1f));
        }

        [Test]
        public void GamepadRightStick_AppliesKeyboardEquivalentYaw()
        {
            gamepad = InputSystem.AddDevice<Gamepad>();
            InputSystem.QueueStateEvent(
                gamepad,
                new GamepadState { rightStick = Vector2.right });
            InputSystem.Update();

            followCamera.ApplyOrbitInput(gamepad.rightStick.ReadValue(), 1f);

            Assert.That(followCamera.CurrentYaw, Is.EqualTo(90f).Within(0.0001f));
        }

        [Test]
        public void OrbitAction_BindsDualSenseRightStickThroughGamepadLayout()
        {
            dualSense = InputSystem.AddDevice<DualSenseGamepadHID>();
            InputAction orbitAction = GetOrbitAction();
            orbitAction.Enable();

            Assert.That(orbitAction.controls, Does.Contain(dualSense.rightStick));
        }

        private InputAction GetOrbitAction()
        {
            MethodInfo ensureOrbitAction = typeof(ThirdPersonFollowCamera).GetMethod(
                "EnsureOrbitAction",
                BindingFlags.Instance | BindingFlags.NonPublic);
            ensureOrbitAction.Invoke(followCamera, null);
            FieldInfo orbitActionField = typeof(ThirdPersonFollowCamera).GetField(
                "orbitAction",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return (InputAction)orbitActionField.GetValue(followCamera);
        }

        private static void AssertVector2Approximately(Vector2 actual, Vector2 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
        }

        private static void AssertVectorApproximately(Vector3 actual, Vector3 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f));
        }
    }
}
