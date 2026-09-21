using System.Collections.Generic;
using NetworkExample.UnityDemo.Shatter;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    /// <summary>
    /// Pins the baked tower chunks against the model they were baked from.
    /// </summary>
    /// <remarks>
    /// The bake is a committed asset, so nothing re-runs it when the model
    /// changes: a new tower would leave the chunks describing the old one, and
    /// the nest would come down as a building nobody recognises. These tests are
    /// what says so. Rebuild with Network Example/Presentation/Build Tower
    /// Shatter Assets, on a machine with Tools/BlastSpike built -- without it the
    /// bake still works, but it is not this one.
    /// </remarks>
    public sealed class TowerShatterAssetTests
    {
        private const string TowerPrefabResource = "Props/tower/tower";
        private const string ChunkSetResource = "Props/tower/tower-shattered-chunks";
        private const string ShatteredPrefabResource = "Props/tower/tower-shattered";

        /// <summary>
        /// The 108 parts the tower is modelled in, with the four that reach
        /// further than <see cref="MeshIslandFracturer.DefaultCutAboveExtent"/>
        /// cut down: the two closed ones by Blast (the roof into its voronoi
        /// cells, the ground into noisy slices), the two open ones by the
        /// project's own clipper. Stated here so a bake that quietly changes is
        /// caught rather than committed.
        /// </summary>
        private const int TowerChunkCount = 138;

        private const string InteriorMaterialPath = "Assets/Resources/Props/tower/tower-interior.mat";

        [Test]
        public void ChunkSet_HoldsEveryPartOfTheModel()
        {
            ShatterChunkSet chunkSet = LoadChunkSet();
            Mesh source = SourceMesh();

            Assert.That(chunkSet.ChunkCount, Is.EqualTo(TowerChunkCount));
            Assert.That(chunkSet.Pivots.Length, Is.EqualTo(chunkSet.ChunkCount));
            Assert.That(chunkSet.SourceMesh, Is.SameAs(source));

            int triangles = 0;
            for (int index = 0; index < chunkSet.ChunkCount; ++index)
            {
                Assert.That(chunkSet.Chunks[index], Is.Not.Null, "chunk " + index);
                triangles += chunkSet.Chunks[index].triangles.Length / 3;
            }

            // Every triangle of the model belongs to exactly one chunk, and a
            // cut adds the faces that close it -- so the chunks hold more than
            // the model, never fewer.
            Assert.That(triangles, Is.GreaterThanOrEqualTo(source.triangles.Length / 3));
            Assert.That(
                triangles,
                Is.LessThan(source.triangles.Length / 3 * 2),
                "Cutting has doubled the model. Something is cutting far more " +
                "than the parts that are too big to throw whole, or Blast's " +
                "noise is being sampled far finer than it needs to be.");
        }

        [Test]
        public void ChunkSet_FillsTheModelsSilhouette()
        {
            ShatterChunkSet chunkSet = LoadChunkSet();
            Mesh source = SourceMesh();

            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int index = 0; index < chunkSet.ChunkCount; ++index)
            {
                Bounds bounds = chunkSet.Chunks[index].bounds;
                Vector3 pivot = chunkSet.Pivots[index];
                min = Vector3.Min(min, pivot + bounds.min);
                max = Vector3.Max(max, pivot + bounds.max);
            }

            Assert.That(Vector3.Distance(min, source.bounds.min), Is.LessThan(0.001f));
            Assert.That(Vector3.Distance(max, source.bounds.max), Is.LessThan(0.001f));
        }

        [Test]
        public void ChunkSet_WasBakedFromTheModelAsItIsNow()
        {
            // The bake cannot be run again here and compared -- part of it runs
            // in Blast, which a machine running tests need not have -- so the
            // chunk set records the model it was cut from instead.
            Assert.That(
                LoadChunkSet().SourceFingerprint,
                Is.EqualTo(ShatterChunkSet.Fingerprint(SourceMesh())),
                "The model has been changed since the chunks were baked.");
        }

        [Test]
        public void ChunkSet_KeepsTheFacesACutMadeOnTheirOwnSubmesh()
        {
            ShatterChunkSet chunkSet = LoadChunkSet();

            int withInterior = 0;
            for (int index = 0; index < chunkSet.ChunkCount; ++index)
            {
                Mesh chunk = chunkSet.Chunks[index];
                Assert.That(chunk.subMeshCount, Is.InRange(1, 2), "chunk " + index);
                if (chunk.subMeshCount == 2)
                {
                    ++withInterior;
                    Assert.That(chunk.GetTriangles(1).Length, Is.GreaterThan(0), "chunk " + index);
                }
            }

            // Only Blast makes interior faces, and it cut the roof and the ground.
            Assert.That(withInterior, Is.GreaterThan(0));
        }

        [Test]
        public void ShatteredPrefab_StandsEveryChunkWhereTheTowerDrawsIt()
        {
            ShatterChunkSet chunkSet = LoadChunkSet();
            Transform towerNode = SourceFilter().transform;
            Transform shatteredNode = ShatteredGeometryNode();

            Assert.That(shatteredNode.name, Is.EqualTo(towerNode.name));
            Assert.That(shatteredNode.localPosition, Is.EqualTo(towerNode.localPosition));
            Assert.That(shatteredNode.localRotation, Is.EqualTo(towerNode.localRotation));
            Assert.That(shatteredNode.localScale, Is.EqualTo(towerNode.localScale));
            Assert.That(shatteredNode.childCount, Is.EqualTo(chunkSet.ChunkCount));

            Material towerMaterial = SourceFilter()
                .GetComponent<MeshRenderer>()
                .sharedMaterial;
            var interiorMaterial =
                UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(InteriorMaterialPath);
            for (int index = 0; index < shatteredNode.childCount; ++index)
            {
                Transform chunk = shatteredNode.GetChild(index);
                Assert.That(chunk.localPosition, Is.EqualTo(chunkSet.Pivots[index]),
                    "chunk " + index);
                Assert.That(chunk.localRotation, Is.EqualTo(Quaternion.identity));
                Assert.That(chunk.localScale, Is.EqualTo(Vector3.one));
                Assert.That(
                    chunk.GetComponent<MeshFilter>().sharedMesh,
                    Is.SameAs(chunkSet.Chunks[index]),
                    "chunk " + index);
                Assert.That(
                    chunk.GetComponent<MeshRenderer>().sharedMaterial,
                    Is.SameAs(towerMaterial),
                    "Chunks share the model's atlas, so nothing needs a material " +
                    "of its own.");
                Assert.That(chunk.GetComponent<Collider>(), Is.Null,
                    "A chunk is drawn, not simulated.");
                Material[] materials = chunk.GetComponent<MeshRenderer>().sharedMaterials;
                int submeshes = chunk.GetComponent<MeshFilter>().sharedMesh.subMeshCount;
                Assert.That(materials.Length, Is.EqualTo(submeshes), "chunk " + index);
                if (submeshes == 2)
                {
                    Assert.That(materials[1], Is.SameAs(interiorMaterial),
                        "chunk " + index + " draws its cut faces with the interior material");
                }
            }
        }

        private static ShatterChunkSet LoadChunkSet()
        {
            var chunkSet = Resources.Load<ShatterChunkSet>(ChunkSetResource);
            Assert.That(chunkSet, Is.Not.Null,
                "No chunk set at Resources/" + ChunkSetResource + ".");
            return chunkSet;
        }

        private static MeshFilter SourceFilter()
        {
            var tower = Resources.Load<GameObject>(TowerPrefabResource);
            Assert.That(tower, Is.Not.Null,
                "No tower prefab at Resources/" + TowerPrefabResource + ".");
            MeshFilter filter = tower.GetComponentInChildren<MeshFilter>(true);
            Assert.That(filter, Is.Not.Null, "The tower prefab draws no mesh.");
            return filter;
        }

        private static Mesh SourceMesh()
        {
            return SourceFilter().sharedMesh;
        }

        private static Transform ShatteredGeometryNode()
        {
            var shattered = Resources.Load<GameObject>(ShatteredPrefabResource);
            Assert.That(shattered, Is.Not.Null,
                "No shattered prefab at Resources/" + ShatteredPrefabResource + ".");
            Assert.That(shattered.transform.childCount, Is.EqualTo(1),
                "The shattered prefab repeats the tower's one model node.");
            return shattered.transform.GetChild(0);
        }
    }
}
