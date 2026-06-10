using NetworkExample.Kernel;
using NetworkExample.UnityDemo.Rendering;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class NetworkRenderingTests
    {
        private GameObject gameObject;
        private GameObject rootObject;
        private NetworkEntityRegistry entityRegistry;
        private NetworkPrefabRegistry prefabRegistry;
        private NetworkRenderStateApplier applier;

        [SetUp]
        public void SetUp()
        {
            gameObject = new GameObject("NetworkRenderingTests");
            rootObject = new GameObject("NetworkRenderingTestsRoot");
            entityRegistry = gameObject.AddComponent<NetworkEntityRegistry>();
            prefabRegistry = gameObject.AddComponent<NetworkPrefabRegistry>();
            applier = gameObject.AddComponent<NetworkRenderStateApplier>();
            applier.Configure(entityRegistry, prefabRegistry, rootObject.transform);
        }

        [TearDown]
        public void TearDown()
        {
            if (gameObject != null)
            {
                Object.DestroyImmediate(gameObject);
            }

            if (rootObject != null)
            {
                Object.DestroyImmediate(rootObject);
            }
        }

        [Test]
        public void InstantiateVisual_WithKnownAbiV15EntityTypes_CreatesPlaceholders()
        {
            AssertPlaceholder(KernelEntityType.Player);
            AssertPlaceholder(KernelEntityType.Enemy);
            AssertPlaceholder(KernelEntityType.Projectile);
            GameObject areaEffect = AssertPlaceholder(KernelEntityType.AreaEffect);
            GameObject beam = AssertPlaceholder(KernelEntityType.Beam);

            Assert.That(areaEffect.transform.localScale.y, Is.LessThan(areaEffect.transform.localScale.x));
            Assert.That(beam.transform.localScale.z, Is.GreaterThan(beam.transform.localScale.x));
        }

        [Test]
        public void Apply_WithAreaEffectAndBeamStates_RegistersVisualsWithoutProjectileViews()
        {
            var states = new[]
            {
                State(100, KernelEntityType.AreaEffect, new KernelVec3(1f, 0f, 2f)),
                State(101, KernelEntityType.Beam, new KernelVec3(3f, 0f, 4f)),
            };

            applier.Apply(states, states.Length);

            Assert.That(entityRegistry.Contains(100), Is.True);
            Assert.That(entityRegistry.Contains(101), Is.True);
            Assert.That(entityRegistry.TryGet(100, out GameObject areaEffectVisual), Is.True);
            Assert.That(entityRegistry.TryGet(101, out GameObject beamVisual), Is.True);
            Assert.That(areaEffectVisual.GetComponent<NetworkProjectileView>(), Is.Null);
            Assert.That(beamVisual.GetComponent<NetworkProjectileView>(), Is.Null);
        }

        private GameObject AssertPlaceholder(KernelEntityType entityType)
        {
            GameObject visual = prefabRegistry.InstantiateVisual(entityType, rootObject.transform);

            Assert.That(visual, Is.Not.Null);
            Assert.That(visual.name, Is.EqualTo("NetEntity_" + entityType));
            Assert.That(visual.transform.parent, Is.EqualTo(rootObject.transform));
            return visual;
        }

        private static RenderEntityState State(
            uint netId,
            KernelEntityType entityType,
            KernelVec3 position)
        {
            return new RenderEntityState
            {
                net_id = netId,
                entity_type = entityType,
                position = position,
                rotation = new KernelQuat(0f, 0f, 0f, 1f),
            };
        }
    }
}
