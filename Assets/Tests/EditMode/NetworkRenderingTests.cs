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
        public void InstantiateVisual_WithKnownAbi34EntityTypes_CreatesPlaceholders()
        {
            AssertPlaceholder(KernelEntityType.Player);
            AssertPlaceholder(KernelEntityType.Projectile);
            GameObject agent = AssertPlaceholder(
                State(1, KernelEntityType.Actor, new KernelVec3(), KernelActorType.Agent));

            Assert.That(agent.GetComponent<NetworkProjectileView>(), Is.Null);
        }

        [Test]
        public void Apply_WithActorStates_RegistersVisualsWithoutProjectileViews()
        {
            var states = new[]
            {
                State(100, KernelEntityType.Actor, new KernelVec3(1f, 0f, 2f), KernelActorType.Player),
                State(101, KernelEntityType.Actor, new KernelVec3(3f, 0f, 4f), KernelActorType.Agent),
            };

            applier.Apply(states, states.Length);

            Assert.That(entityRegistry.Contains(100), Is.True);
            Assert.That(entityRegistry.Contains(101), Is.True);
            Assert.That(entityRegistry.TryGet(100, out GameObject playerVisual), Is.True);
            Assert.That(entityRegistry.TryGet(101, out GameObject agentVisual), Is.True);
            Assert.That(playerVisual.GetComponent<NetworkProjectileView>(), Is.Null);
            Assert.That(agentVisual.GetComponent<NetworkProjectileView>(), Is.Null);
        }

        [Test]
        public void Apply_WithPredictedProjectileState_RegistersVisualByClientActionId()
        {
            var states = new[]
            {
                new RenderEntityState
                {
                    entity_type = KernelEntityType.Projectile,
                    client_action_id = 42,
                    position = new KernelVec3(5f, 0f, 6f),
                    rotation = new KernelQuat(0f, 0f, 0f, 1f),
                    status = RenderEntityStatus.Predicted,
                },
            };

            applier.Apply(states, states.Length);

            Assert.That(rootObject.transform.childCount, Is.EqualTo(1));
            GameObject projectileVisual = rootObject.transform.GetChild(0).gameObject;
            NetworkProjectileView projectileView = projectileVisual.GetComponent<NetworkProjectileView>();
            Assert.That(projectileView, Is.Not.Null);
            Assert.That(projectileView.ClientActionId, Is.EqualTo(42));
            Assert.That(projectileVisual.name, Is.EqualTo("PredictedProjectile_42"));
            Assert.That(projectileVisual.transform.position, Is.EqualTo(new Vector3(5f, 0f, 6f)));
        }

        [Test]
        public void Apply_WithPredictedProjectileBoundToServerNetId_UpdatesExistingVisual()
        {
            var predictedState = new RenderEntityState
            {
                entity_type = KernelEntityType.Projectile,
                client_action_id = 42,
                position = new KernelVec3(5f, 0f, 6f),
                rotation = new KernelQuat(0f, 0f, 0f, 1f),
                status = RenderEntityStatus.Predicted,
            };
            var boundState = predictedState;
            boundState.net_id = 402;
            boundState.position = new KernelVec3(7f, 0f, 8f);

            applier.Apply(new[] { predictedState }, 1);
            GameObject projectileVisual = rootObject.transform.GetChild(0).gameObject;

            applier.Apply(new[] { boundState }, 1);

            NetworkProjectileView projectileView = projectileVisual.GetComponent<NetworkProjectileView>();
            Assert.That(projectileView.ServerEntityId, Is.EqualTo(402));
            Assert.That(projectileVisual.name, Is.EqualTo("NetProjectile_402"));
            Assert.That(projectileVisual.transform.position, Is.EqualTo(new Vector3(7f, 0f, 8f)));
        }

        private GameObject AssertPlaceholder(KernelEntityType entityType)
        {
            GameObject visual = prefabRegistry.InstantiateVisual(entityType, rootObject.transform);

            Assert.That(visual, Is.Not.Null);
            Assert.That(visual.name, Is.EqualTo("NetEntity_" + entityType));
            Assert.That(visual.transform.parent, Is.EqualTo(rootObject.transform));
            return visual;
        }

        private GameObject AssertPlaceholder(RenderEntityState state)
        {
            GameObject visual = prefabRegistry.InstantiateVisual(state, rootObject.transform);

            Assert.That(visual, Is.Not.Null);
            Assert.That(visual.transform.parent, Is.EqualTo(rootObject.transform));
            return visual;
        }

        private static RenderEntityState State(
            uint netId,
            KernelEntityType entityType,
            KernelVec3 position,
            KernelActorType actorType = KernelActorType.Unknown)
        {
            return new RenderEntityState
            {
                net_id = netId,
                entity_type = entityType,
                actor_type = actorType,
                position = position,
                rotation = new KernelQuat(0f, 0f, 0f, 1f),
            };
        }
    }
}
