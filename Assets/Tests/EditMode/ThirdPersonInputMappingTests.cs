using NetworkExample.UnityDemo.Input;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class ThirdPersonInputMappingTests
    {
        private GameObject samplerObject;
        private GameObject viewObject;
        private NetworkInputSampler sampler;

        [SetUp]
        public void SetUp()
        {
            samplerObject = new GameObject("ThirdPersonInputMappingTests");
            viewObject = new GameObject("ThirdPersonInputView");
            sampler = samplerObject.AddComponent<NetworkInputSampler>();
            sampler.SetViewTransform(viewObject.transform);
        }

        [TearDown]
        public void TearDown()
        {
            if (viewObject != null)
            {
                Object.DestroyImmediate(viewObject);
            }

            if (samplerObject != null)
            {
                Object.DestroyImmediate(samplerObject);
            }
        }

        [Test]
        public void TransformMove_WithNinetyDegreeCameraYaw_MapsForwardToWorldPositiveX()
        {
            viewObject.transform.rotation = Quaternion.Euler(0f, 90f, 0f);

            Vector2 move = sampler.TransformMoveToWorld(Vector2.up);

            Assert.That(move.x, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(move.y, Is.EqualTo(0f).Within(0.0001f));
        }

        [Test]
        public void TransformMove_WithNinetyDegreeCameraYaw_MapsRightToWorldNegativeZ()
        {
            viewObject.transform.rotation = Quaternion.Euler(0f, 90f, 0f);

            Vector2 move = sampler.TransformMoveToWorld(Vector2.right);

            Assert.That(move.x, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(move.y, Is.EqualTo(-1f).Within(0.0001f));
        }

        [Test]
        public void TransformMove_WithDiagonalInput_ClampsMagnitude()
        {
            viewObject.transform.rotation = Quaternion.Euler(0f, 37f, 0f);

            Vector2 move = sampler.TransformMoveToWorld(Vector2.one);

            Assert.That(move.magnitude, Is.EqualTo(1f).Within(0.0001f));
        }

        [Test]
        public void GetAimDirection_WithCameraPitchAndYaw_UsesFullCameraForward()
        {
            Quaternion rotation = Quaternion.Euler(-18f, 42f, 0f);
            viewObject.transform.rotation = rotation;

            Vector3 aim = sampler.GetAimDirection(Vector2.zero);
            Vector3 expected = rotation * Vector3.forward;

            Assert.That(aim.x, Is.EqualTo(expected.x).Within(0.0001f));
            Assert.That(aim.y, Is.EqualTo(expected.y).Within(0.0001f));
            Assert.That(aim.z, Is.EqualTo(expected.z).Within(0.0001f));
        }
    }
}
