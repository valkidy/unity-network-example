using System.Collections;
using NetworkExample.UnityDemo.Text3D;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NetworkExample.UnityDemo.Tests.PlayMode
{
    public sealed class WaxGlyphTextTests
    {
        [UnityTest]
        public IEnumerator Start_BuildsGlyphsOffTheMainThreadAndUploadsInSlices()
        {
            var root = new GameObject("WaxGlyphTextTests");
            try
            {
                var glyphText = root.AddComponent<WaxGlyphText>();
                glyphText.Text = "Wax Candy 0123456789";

                float deadline = Time.realtimeSinceStartup + 60f;
                yield return null;
                while ((glyphText.IsBuilding || glyphText.LastReport == null) && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                WaxGlyphBuildReport report = glyphText.LastReport;
                Assert.That(report, Is.Not.Null, "The build did not finish within a minute.");
                Debug.Log(report);

                Assert.That(report.BackgroundThreadId, Is.Not.EqualTo(report.MainThreadId), "glyphs were built on the main thread");
                Assert.That(report.MissingCharacters, Is.Zero);
                Assert.That(report.MaxTrianglesPerGlyph, Is.LessThanOrEqualTo(5000));
                Assert.That(report.Glyphs.Count, Is.EqualTo(17), "distinct characters: W a x C n d y and ten digits");
                Assert.That(root.transform.childCount, Is.EqualTo(18), "one object per non-space character");
                Assert.That(report.MaxMainThreadSliceMilliseconds, Is.LessThan(50.0), "longest main-thread slice");

                foreach (Transform child in root.transform)
                {
                    Material material = child.GetComponent<MeshRenderer>().sharedMaterial;
                    Assert.That(material.GetTexture(WaxGlyphAssets.EdgeDistanceMapId), Is.Not.Null, child.name);
                    Assert.That(child.GetComponent<MeshFilter>().sharedMesh, Is.Not.Null, child.name);
                }
            }
            finally
            {
                Object.Destroy(root);
            }
        }
    }
}
