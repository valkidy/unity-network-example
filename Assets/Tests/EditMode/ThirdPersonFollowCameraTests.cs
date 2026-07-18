using NetworkExample.UnityDemo.CameraSystem;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class ThirdPersonFollowCameraTests
    {
        private GameObject cameraObject;
        private GameObject targetObject;
        private GameObject secondTargetObject;
        private ThirdPersonFollowCamera followCamera;

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
                targetObject.transform.position + new Vector3(0.6f, 1.45f, 0f);
            Vector3 expectedPosition =
                expectedShoulderPivot - expectedRotation * Vector3.forward * 4.5f;
            Camera controlledCamera = cameraObject.GetComponent<Camera>();

            AssertVectorApproximately(cameraObject.transform.position, expectedPosition);
            Assert.That(
                Quaternion.Angle(cameraObject.transform.rotation, expectedRotation),
                Is.LessThan(0.001f));
            Assert.That(controlledCamera.fieldOfView, Is.EqualTo(65f).Within(0.0001f));
            Assert.That(controlledCamera.nearClipPlane, Is.EqualTo(0.1f).Within(0.0001f));
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

        private static void AssertVectorApproximately(Vector3 actual, Vector3 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f));
        }
    }
}
