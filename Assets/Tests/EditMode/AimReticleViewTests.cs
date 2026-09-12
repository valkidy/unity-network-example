using NetworkExample.UnityDemo.UI;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class AimReticleViewTests
    {
        private GameObject hostObject;
        private AimReticleView reticle;

        [SetUp]
        public void SetUp()
        {
            hostObject = new GameObject("AimReticleViewTests");
            reticle = hostObject.AddComponent<AimReticleView>();
        }

        [TearDown]
        public void TearDown()
        {
            if (hostObject != null)
            {
                Object.DestroyImmediate(hostObject);
            }
        }

        /// <summary>
        /// The reticle is placed by anchor rather than by pixel offset, so the
        /// viewport point it is given has to survive as the anchor unchanged --
        /// that is what keeps it in the same place at every resolution.
        /// </summary>
        [Test]
        public void UpdateReticle_PlacesTheRootByAnchorAtTheViewportPoint()
        {
            reticle.UpdateReticle(1f, new Vector2(0.63f, 0.5f));

            RectTransform root = reticle.Root;
            Assert.That(root, Is.Not.Null);
            Assert.That(root.anchorMin.x, Is.EqualTo(0.63f).Within(0.0001f));
            Assert.That(root.anchorMin.y, Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(root.anchorMax, Is.EqualTo(root.anchorMin));
            Assert.That(root.anchoredPosition, Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void UpdateReticle_TightensAndBrightensAcrossTheAimBlend()
        {
            reticle.UpdateReticle(0f, new Vector2(0.5f, 0.5f));
            float hipGap = reticle.CurrentGap;
            float hipAlpha = reticle.CurrentAlpha;

            reticle.UpdateReticle(1f, new Vector2(0.5f, 0.5f));

            Assert.That(reticle.CurrentGap, Is.LessThan(hipGap));
            Assert.That(reticle.CurrentAlpha, Is.GreaterThan(hipAlpha));
        }

        [Test]
        public void UpdateReticle_HalfwayThroughTheBlend_SitsBetweenBothStyles()
        {
            reticle.UpdateReticle(0f, new Vector2(0.5f, 0.5f));
            float hipGap = reticle.CurrentGap;
            reticle.UpdateReticle(1f, new Vector2(0.5f, 0.5f));
            float aimGap = reticle.CurrentGap;

            reticle.UpdateReticle(0.5f, new Vector2(0.5f, 0.5f));

            Assert.That(
                reticle.CurrentGap,
                Is.EqualTo((hipGap + aimGap) * 0.5f).Within(0.0001f));
        }

        [Test]
        public void UpdateReticle_BuildsFourTicksThatStayCentredOnTheReticle()
        {
            reticle.UpdateReticle(1f, new Vector2(0.5f, 0.5f));

            RectTransform root = reticle.Root;
            // Four ticks plus the centre dot.
            Assert.That(root.childCount, Is.EqualTo(5));

            Vector2 sum = Vector2.zero;
            for (int index = 0; index < 4; ++index)
            {
                sum += ((RectTransform)root.GetChild(index)).anchoredPosition;
            }

            Assert.That(sum.magnitude, Is.LessThan(0.0001f));
        }
    }
}
