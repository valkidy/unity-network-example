using NetworkExample.UnityDemo.Items;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class ItemPropAimDirectionTests
    {
        private GameObject controllerObject;
        private GameObject viewObject;
        private NetworkItemPropController controller;

        [SetUp]
        public void SetUp()
        {
            controllerObject = new GameObject("ItemPropAimDirectionTests");
            viewObject = new GameObject("View");
            controller = controllerObject.AddComponent<NetworkItemPropController>();
            controller.Configure(null, viewObject.transform);
        }

        [TearDown]
        public void TearDown()
        {
            if (viewObject != null)
            {
                Object.DestroyImmediate(viewObject);
            }

            if (controllerObject != null)
            {
                Object.DestroyImmediate(controllerObject);
            }
        }

        [Test]
        public void ResolveAimDirection_WithNoReticleDirection_FallsBackToTheViewForward()
        {
            viewObject.transform.rotation = Quaternion.Euler(0f, 90f, 0f);

            Vector3 direction = controller.ResolveAimDirection();

            Assert.That(direction.x, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(direction.z, Is.EqualTo(0f).Within(0.0001f));
        }

        /// <summary>
        /// The reticle is allowed to sit off centre, so camera forward is not the
        /// line a shot travels. A throw has to follow the reticle or it leaves at a
        /// visibly different angle from the one the player is aiming along.
        /// </summary>
        [Test]
        public void ResolveAimDirection_WithAReticleDirection_PrefersItOverTheViewForward()
        {
            viewObject.transform.rotation = Quaternion.identity;
            controller.SetAimDirection(new Vector3(1f, 0f, 1f));

            Vector3 direction = controller.ResolveAimDirection();

            Assert.That(direction.x, Is.EqualTo(0.70710678f).Within(0.0001f));
            Assert.That(direction.z, Is.EqualTo(0.70710678f).Within(0.0001f));
        }

        [Test]
        public void ResolveAimDirection_KeepsThePitchOfTheReticleRay()
        {
            controller.SetAimDirection(new Vector3(0f, 1f, 1f));

            Vector3 direction = controller.ResolveAimDirection();

            Assert.That(direction.y, Is.EqualTo(0.70710678f).Within(0.0001f));
        }

        [Test]
        public void ResolveAimDirection_WithADegenerateReticleDirection_FallsBack()
        {
            viewObject.transform.rotation = Quaternion.Euler(0f, 90f, 0f);

            controller.SetAimDirection(Vector3.zero);
            Assert.That(controller.ResolveAimDirection().x, Is.EqualTo(1f).Within(0.0001f));

            controller.SetAimDirection(new Vector3(float.NaN, 0f, 0f));
            Assert.That(controller.ResolveAimDirection().x, Is.EqualTo(1f).Within(0.0001f));
        }
    }
}
