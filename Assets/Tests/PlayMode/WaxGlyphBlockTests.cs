using System.Collections;
using NetworkExample.UnityDemo.Text3D;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NetworkExample.UnityDemo.Tests.PlayMode
{
    public sealed class WaxGlyphBlockTests
    {
        [UnityTest]
        public IEnumerator AssignSeed_BuildsThePickedGlyphInsideTheBoxAndSharesIt()
        {
            const ulong seed = 12345;
            var first = new GameObject("WaxGlyphBlockTests A");
            var second = new GameObject("WaxGlyphBlockTests B");
            try
            {
                var firstBlock = first.AddComponent<WaxGlyphBlock>();
                var secondBlock = second.AddComponent<WaxGlyphBlock>();
                firstBlock.AssignSeed(seed);
                secondBlock.AssignSeed(seed);

                float deadline = Time.realtimeSinceStartup + 60f;
                while ((firstBlock.GlyphObject == null || secondBlock.GlyphObject == null) && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.That(firstBlock.GlyphObject, Is.Not.Null, "The glyph was not built within a minute.");
                Assert.That(secondBlock.GlyphObject, Is.Not.Null, "The second glyph was not built within a minute.");

                int expected = GlyphCharacterSet.Parse(WaxGlyphBlock.DefaultCharacterSet).Pick(seed);
                Assert.That(firstBlock.Codepoint, Is.EqualTo(expected));
                Assert.That(
                    firstBlock.GlyphObject.transform.localRotation,
                    Is.EqualTo(Quaternion.identity),
                    "an unturned glyph reads from -Z, the side the thrower stands on");
                Assert.That(
                    firstBlock.GlyphObject.GetComponent<MeshRenderer>().GetShaderUserValue(),
                    Is.EqualTo(WaxGlyphBlock.FlowStartUserValue(firstBlock.FlowStartTime)),
                    "the glyph's renderer carries when its melt flow starts");
                Assert.That(
                    secondBlock.GlyphObject.GetComponent<MeshFilter>().sharedMesh,
                    Is.SameAs(firstBlock.GlyphObject.GetComponent<MeshFilter>().sharedMesh),
                    "blocks showing the same character share its mesh");

                // The block stands at the origin unrotated, so world bounds are box space.
                Bounds bounds = firstBlock.GlyphObject.GetComponent<MeshRenderer>().bounds;
                Vector3 box = firstBlock.BoxSize;
                const float tolerance = 1e-3f;
                Assert.That(bounds.min.y, Is.GreaterThanOrEqualTo(-tolerance), "stands on its bottom");
                Assert.That(bounds.max.y, Is.LessThanOrEqualTo(box.y + tolerance));
                Assert.That(Mathf.Abs(bounds.center.x), Is.LessThan(tolerance), "centered across");
                Assert.That(bounds.extents.x, Is.LessThanOrEqualTo(0.5f * box.x + tolerance));
                Assert.That(bounds.extents.z, Is.LessThanOrEqualTo(0.5f * box.z + tolerance));

                Assert.That(firstBlock.PedestalObject, Is.Not.Null, "blocks stand in a pedestal by default");
                Bounds pedestal = firstBlock.PedestalObject.GetComponent<MeshRenderer>().bounds;
                Assert.That(pedestal.min.y, Is.EqualTo(0f).Within(tolerance), "the pedestal lies on the ground");
                Assert.That(Mathf.Max(-pedestal.min.x, pedestal.max.x), Is.LessThanOrEqualTo(0.5f * box.x + tolerance), "pedestal width");
                Assert.That(bounds.min.y, Is.GreaterThan(0f), "the glyph is lifted into the pedestal");
                Assert.That(bounds.min.y, Is.LessThan(pedestal.max.y), "the glyph's bottom is sunk into the pedestal");
            }
            finally
            {
                Object.Destroy(first);
                Object.Destroy(second);
                WaxGlyphLibrary.Clear();
            }
        }
    }
}
