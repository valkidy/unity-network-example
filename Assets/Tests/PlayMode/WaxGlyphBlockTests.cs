using System.Collections;
using NetworkExample.Kernel;
using NetworkExample.UnityDemo.Rendering;
using NetworkExample.UnityDemo.Text3D;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NetworkExample.UnityDemo.Tests.PlayMode
{
    public sealed class WaxGlyphBlockTests
    {
        private const uint GlyphBlockTemplateId = 209;

        [UnityTest]
        public IEnumerator TwoClients_SameNetIds_ShowTheSameCharacters()
        {
            // Two independent presentation stacks stand in for two clients. They receive the same
            // glyph blocks in opposite orders, as a client that joined later or saw them spawn in a
            // different order would, and must still show the same character on every net id,
            // because the character comes from the net id alone. The melt flow may differ; this
            // does not look at it.
            uint[] netIds = { 7, 42, 1001, 65536, 4000000000u };
            var template = new GameObject("GlyphBlockTemplate");
            template.SetActive(false);
            template.AddComponent<WaxGlyphBlock>();
            var catalog = ScriptableObject.CreateInstance<NetworkPrefabCatalog>();
            catalog.Configure(null, null);
            catalog.ConfigureProps(new[] { new NetworkPrefabCatalog.PropPrefabBinding(GlyphBlockTemplateId, template) });
            var clientA = new PresentationClient("A", catalog);
            var clientB = new PresentationClient("B", catalog);
            try
            {
                var inOrder = new RenderEntityState[netIds.Length];
                var reversed = new RenderEntityState[netIds.Length];
                for (int i = 0; i < netIds.Length; i++)
                {
                    inOrder[i] = GlyphBlockState(netIds[i]);
                    reversed[netIds.Length - 1 - i] = GlyphBlockState(netIds[i]);
                }

                clientA.Apply(inOrder);
                clientB.Apply(reversed);
                yield return null;

                GlyphCharacterSet set = GlyphCharacterSet.Parse(WaxGlyphBlock.DefaultCharacterSet);
                foreach (uint netId in netIds)
                {
                    int shownByA = clientA.CodepointOf(netId);
                    int shownByB = clientB.CodepointOf(netId);
                    Assert.That(shownByB, Is.EqualTo(shownByA), $"net id {netId}: both clients show one character");
                    Assert.That(shownByA, Is.EqualTo(set.Pick(netId)), $"net id {netId}: the character its net id picks");
                }
            }
            finally
            {
                clientA.Dispose();
                clientB.Dispose();
                Object.Destroy(template);
                Object.Destroy(catalog);
                WaxGlyphLibrary.Clear();
            }
        }

        private static RenderEntityState GlyphBlockState(uint netId) => new RenderEntityState
        {
            net_id = netId,
            entity_type = KernelEntityType.Prop,
            template_id = GlyphBlockTemplateId,
            rotation = new KernelQuat(0f, 0f, 0f, 1f),
        };

        // The client-side path a replicated glyph block takes: render state applier, prefab
        // registry and entity registry, as ClientRunner and HostModeRunner wire them.
        private sealed class PresentationClient : System.IDisposable
        {
            private readonly GameObject root;
            private readonly NetworkEntityRegistry entities;
            private readonly NetworkRenderStateApplier applier;

            public PresentationClient(string name, NetworkPrefabCatalog catalog)
            {
                root = new GameObject($"Client {name}");
                entities = root.AddComponent<NetworkEntityRegistry>();
                var prefabs = root.AddComponent<NetworkPrefabRegistry>();
                prefabs.Configure(catalog);
                applier = root.AddComponent<NetworkRenderStateApplier>();
                applier.Configure(entities, prefabs, root.transform);
            }

            public void Apply(RenderEntityState[] states) => applier.Apply(states, states.Length);

            public int CodepointOf(uint netId)
            {
                Assert.That(entities.TryGetByNetId(netId, out GameObject visual), Is.True, $"no visual for net id {netId}");
                WaxGlyphBlock block = visual.GetComponent<WaxGlyphBlock>();
                Assert.That(block, Is.Not.Null, $"net id {netId} is not a glyph block");
                return block.Codepoint;
            }

            public void Dispose() => Object.Destroy(root);
        }

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
