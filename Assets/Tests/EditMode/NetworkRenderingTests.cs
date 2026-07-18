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

        [TestCase(KernelActorType.Player, 0.9f, 0.7f)]
        [TestCase(KernelActorType.Agent, 0.8f, 0.8f)]
        public void InstantiateVisual_WithActorPlaceholder_AlignsCapsuleBottomToGround(
            KernelActorType actorType,
            float expectedCenterHeight,
            float expectedDiameter)
        {
            GameObject visual = AssertPlaceholder(
                State(1, KernelEntityType.Actor, new KernelVec3(), actorType));

            Assert.That(visual.transform.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(visual.transform.childCount, Is.EqualTo(1));

            Transform capsule = visual.transform.GetChild(0);
            Assert.That(capsule.localPosition, Is.EqualTo(Vector3.up * expectedCenterHeight));
            Assert.That(
                capsule.localScale,
                Is.EqualTo(new Vector3(
                    expectedDiameter,
                    expectedCenterHeight,
                    expectedDiameter)));

            MeshFilter meshFilter = capsule.GetComponent<MeshFilter>();
            Assert.That(meshFilter, Is.Not.Null);
            float bottom = capsule.localPosition.y +
                meshFilter.sharedMesh.bounds.min.y * capsule.localScale.y;
            Assert.That(bottom, Is.EqualTo(0f).Within(0.0001f));
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
        public void Registry_WithDistinctEntityAndNetIds_IndexesVisualByBothIdDomains()
        {
            var entityEleven = new GameObject("Entity11_Net2997");
            var entityTwelve = new GameObject("Entity12_Net11");
            entityEleven.transform.SetParent(rootObject.transform);
            entityTwelve.transform.SetParent(rootObject.transform);
            entityRegistry.Register(11, entityEleven);
            entityRegistry.RegisterNetId(2997, entityEleven);
            entityRegistry.Register(12, entityTwelve);
            entityRegistry.RegisterNetId(11, entityTwelve);

            Assert.That(
                entityRegistry.TryGet(11, out GameObject entityKeyEleven),
                Is.True);
            Assert.That(
                entityRegistry.TryGetByNetId(2997, out GameObject netId2997),
                Is.True);
            Assert.That(netId2997, Is.SameAs(entityKeyEleven));

            Assert.That(
                entityRegistry.TryGet(12, out GameObject entityKeyTwelve),
                Is.True);
            Assert.That(
                entityRegistry.TryGetByNetId(11, out GameObject netIdEleven),
                Is.True);
            Assert.That(netIdEleven, Is.SameAs(entityKeyTwelve));
            Assert.That(netIdEleven, Is.Not.SameAs(entityKeyEleven));
        }

        [Test]
        public void Apply_WithPredictedProjectileState_RegistersVisualByActionInstanceId()
        {
            var states = new[]
            {
                new RenderEntityState
                {
                    entity_type = KernelEntityType.Projectile,
                    action_instance_id = 42,
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
            Assert.That(projectileView.ActionInstanceId, Is.EqualTo(42));
            Assert.That(projectileVisual.name, Is.EqualTo("PredictedProjectile_42"));
            Assert.That(projectileVisual.transform.position, Is.EqualTo(new Vector3(5f, 0f, 6f)));
        }

        [Test]
        public void Apply_WithPredictedProjectileBoundToServerNetId_UpdatesExistingVisual()
        {
            var predictedState = new RenderEntityState
            {
                entity_type = KernelEntityType.Projectile,
                action_instance_id = 42,
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

        [Test]
        public void Apply_WithActorState_DerivesContinuousAnimationFromFlagsAndPhase()
        {
            RenderEntityState state = State(
                100,
                KernelEntityType.Actor,
                new KernelVec3(),
                KernelActorType.Player);
            state.visual_flags = KernelConstants.VisualFlagMoving |
                KernelConstants.VisualFlagAiming;
            state.animation_state = ushort.MaxValue;
            state.action = new KernelActionRuntimeView
            {
                action_instance_id = 77,
                phase = KernelActionPhase.Active,
            };

            applier.Apply(new[] { state }, 1);

            Assert.That(entityRegistry.TryGet(100, out GameObject visual), Is.True);
            NetworkActorView actorView = visual.GetComponent<NetworkActorView>();
            Assert.That(actorView, Is.Not.Null);
            Assert.That(actorView.IsMoving, Is.True);
            Assert.That(actorView.IsAiming, Is.True);
            Assert.That(actorView.IsFiring, Is.True);
            Assert.That(actorView.IsIdle, Is.False);
            Assert.That(actorView.ActionInstanceId, Is.EqualTo(77));
        }

        [Test]
        public void Apply_WithMovingActor_FacesHorizontalVelocity()
        {
            GameObject visual = RegisterActorVisual(100, 100);
            RenderEntityState state = State(
                100,
                KernelEntityType.Actor,
                new KernelVec3(),
                KernelActorType.Player);
            state.velocity = new KernelVec3(1f, 0.5f, 0f);

            applier.Apply(new[] { state }, 1);

            Assert.That(
                Vector3.Angle(visual.transform.forward, Vector3.right),
                Is.LessThan(0.01f));
            Assert.That(
                Vector3.Angle(visual.transform.up, Vector3.up),
                Is.LessThan(0.01f));
        }

        [Test]
        public void Apply_WhenMovingActorStops_PreservesLastMovementFacing()
        {
            GameObject visual = RegisterActorVisual(100, 100);
            RenderEntityState moving = State(
                100,
                KernelEntityType.Actor,
                new KernelVec3(),
                KernelActorType.Player);
            moving.velocity = new KernelVec3(-1f, 0f, 0f);
            applier.Apply(new[] { moving }, 1);

            RenderEntityState stopped = moving;
            stopped.velocity = new KernelVec3();
            stopped.rotation = new KernelQuat(0f, 0f, 0f, 1f);
            applier.Apply(new[] { stopped }, 1);

            Assert.That(
                Vector3.Angle(visual.transform.forward, Vector3.left),
                Is.LessThan(0.01f));
        }

        [Test]
        public void ApplyRemoteActionPresentationEvents_DeduplicatesEveryCommitInRange()
        {
            GameObject visual = RegisterActorVisual(7, 101);
            var remoteEvent = new KernelRemoteActionPresentationEvent
            {
                actor_net_id = 101,
                action_instance_id = 88,
                first_commit_index = 3,
                commit_count = 2,
                event_type = KernelRemoteActionPresentationEventType.FireCommit,
            };

            applier.ApplyRemoteActionPresentationEvents(new[] { remoteEvent }, 1);
            applier.ApplyRemoteActionPresentationEvents(new[] { remoteEvent }, 1);

            Assert.That(visual.GetComponent<NetworkActorView>().RemoteCommitCount, Is.EqualTo(2));
        }

        [Test]
        public void AcceptedLocalActionResult_ConfirmsWithoutReplayingPrediction()
        {
            GameObject visual = RegisterActorVisual(8, 102);
            var intent = new ActionIntent
            {
                action_instance_id = 91,
                binding_id = KernelActionBinding.PrimaryFire,
            };
            applier.BeginPredictedLocalAction(102, intent);

            applier.ApplyLocalActionResults(
                102,
                new[]
                {
                    new KernelLocalActionResult
                    {
                        action_instance_id = 91,
                        result = KernelLocalActionResultType.Accepted,
                    },
                },
                1);

            Assert.That(visual.GetComponent<NetworkActorView>().PredictedCommitCount, Is.EqualTo(1));
        }

        private GameObject RegisterActorVisual(ulong entityId, ulong netId)
        {
            var visual = new GameObject("Actor_" + entityId + "_" + netId);
            visual.transform.SetParent(rootObject.transform);
            visual.AddComponent<NetworkActorView>();
            entityRegistry.Register(entityId, visual);
            entityRegistry.RegisterNetId(netId, visual);
            return visual;
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
