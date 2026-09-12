using System.Collections.Generic;
using NetworkExample.Kernel;
using NetworkExample.UnityDemo.Common;
using NetworkExample.UnityDemo.Rendering;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using RemoteActionTriggerBinding =
    NetworkExample.UnityDemo.Rendering.NetworkActorView.RemoteActionTriggerBinding;

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
        public void InstantiateVisual_WithKnownAbi34EntityTypes_CreatesVisuals()
        {
            AssertPlaceholder(KernelEntityType.Player);
            AssertPlaceholder(KernelEntityType.Projectile);
            GameObject agent = AssertPlaceholder(
                State(1, KernelEntityType.Actor, new KernelVec3(), KernelActorType.Agent));

            Assert.That(agent.GetComponent<NetworkProjectileView>(), Is.Null);
        }

        [Test]
        public void DefaultPrefabCatalog_RegistersCurrentActorAndProjectileTemplateIds()
        {
            NetworkPrefabCatalog catalog = Resources.Load<NetworkPrefabCatalog>(
                NetworkPrefabRegistry.DefaultCatalogResourcePath);

            Assert.That(catalog, Is.Not.Null);
            Assert.That(catalog.TryGetActorPrefab(1, out GameObject player), Is.True);
            Assert.That(catalog.TryGetActorPrefab(2, out GameObject agent), Is.True);
            Assert.That(player, Is.Not.SameAs(agent));
            Assert.That(player.GetComponent<NetworkActorView>(), Is.Not.Null);
            Assert.That(agent.GetComponent<NetworkActorView>(), Is.Not.Null);
            // Both bindings must reach an Animator: NetworkActorView resolves one
            // with GetComponentInChildren and silently no-ops without it, so a
            // catalog pointing at a model with no Animator loses every animation
            // parameter the kernel already replicates -- and reports nothing.
            Assert.That(
                player.GetComponentInChildren<Animator>(true),
                Is.Not.Null,
                "Player actor prefab has no Animator");
            Assert.That(
                agent.GetComponentInChildren<Animator>(true),
                Is.Not.Null,
                "Agent actor prefab has no Animator");

            GameObject sharedProjectile = null;
            GameObject fireFloor = null;
            for (uint templateId = 2; templateId <= 8; ++templateId)
            {
                Assert.That(
                    catalog.TryGetProjectilePrefab(templateId, out GameObject projectile),
                    Is.True,
                    "Missing projectile template " + templateId);
                Assert.That(projectile.GetComponent<NetworkProjectileView>(), Is.Not.Null);
                if (templateId == 4)
                {
                    fireFloor = projectile;
                    continue;
                }

                sharedProjectile = sharedProjectile == null ? projectile : sharedProjectile;
                Assert.That(projectile, Is.SameAs(sharedProjectile));
            }
            Assert.That(fireFloor, Is.Not.Null);
            Assert.That(fireFloor, Is.Not.SameAs(sharedProjectile));
        }

        [Test]
        public void PrefabCatalog_ExactTemplateBindingWinsAndUnknownUsesActorFallback()
        {
            var exactPrefab = new GameObject("ExactActorPrefab");
            var fallbackPrefab = new GameObject("FallbackActorPrefab");
            var catalog = ScriptableObject.CreateInstance<NetworkPrefabCatalog>();
            try
            {
                exactPrefab.transform.localScale = Vector3.one * 2f;
                fallbackPrefab.transform.localScale = Vector3.one * 3f;
                catalog.Configure(
                    new[]
                    {
                        new NetworkPrefabCatalog.ActorPrefabBinding(99, exactPrefab),
                    },
                    null,
                    fallbackPrefab,
                    fallbackPrefab);
                prefabRegistry.Configure(catalog);

                RenderEntityState exactState = State(
                    10,
                    KernelEntityType.Actor,
                    new KernelVec3(),
                    KernelActorType.Player);
                exactState.template_id = 99;
                RenderEntityState fallbackState = State(
                    11,
                    KernelEntityType.Actor,
                    new KernelVec3(),
                    KernelActorType.Player);
                fallbackState.template_id = 100;

                GameObject exact = prefabRegistry.InstantiateVisual(
                    exactState,
                    rootObject.transform);
                GameObject fallback = prefabRegistry.InstantiateVisual(
                    fallbackState,
                    rootObject.transform);

                Assert.That(exact.transform.localScale, Is.EqualTo(Vector3.one * 2f));
                Assert.That(fallback.transform.localScale, Is.EqualTo(Vector3.one * 3f));
            }
            finally
            {
                Object.DestroyImmediate(catalog);
                Object.DestroyImmediate(exactPrefab);
                Object.DestroyImmediate(fallbackPrefab);
                prefabRegistry.Configure(null);
            }
        }

        [Test]
        public void PrefabCatalog_DuplicateIdSkipsNullAndWarnsOnlyDuringValidation()
        {
            var registeredPrefab = new GameObject("RegisteredActorPrefab");
            var catalog = ScriptableObject.CreateInstance<NetworkPrefabCatalog>();
            try
            {
                catalog.Configure(
                    new[]
                    {
                        new NetworkPrefabCatalog.ActorPrefabBinding(77, null),
                        new NetworkPrefabCatalog.ActorPrefabBinding(77, registeredPrefab),
                    },
                    null,
                    registeredPrefab,
                    registeredPrefab);
                prefabRegistry.Configure(catalog);

                LogAssert.Expect(
                    LogType.Warning,
                    "Network prefab catalog actor template id 77 has no prefab.");
                LogAssert.Expect(
                    LogType.Warning,
                    "Network prefab catalog contains duplicate actor template id 77; " +
                    "the first non-null prefab wins.");

                RenderEntityState state = State(
                    10,
                    KernelEntityType.Actor,
                    new KernelVec3(),
                    KernelActorType.Player);
                state.template_id = 77;
                GameObject first = prefabRegistry.InstantiateVisual(
                    state,
                    rootObject.transform);
                GameObject second = prefabRegistry.InstantiateVisual(
                    state,
                    rootObject.transform);

                Assert.That(first, Is.Not.Null);
                Assert.That(second, Is.Not.Null);
            }
            finally
            {
                Object.DestroyImmediate(catalog);
                Object.DestroyImmediate(registeredPrefab);
                prefabRegistry.Configure(null);
            }
        }

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
        public void Apply_WithActorState_MapsSpeedGroundingPhaseAndLocalAim()
        {
            RenderEntityState state = State(
                100,
                KernelEntityType.Actor,
                new KernelVec3(),
                KernelActorType.Player);
            state.template_id = 1;
            state.velocity = new KernelVec3(3f, 99f, 4f);
            // Pitched aim on purpose. The action phase below makes facing
            // aim-driven, so the body absorbs the aim's yaw and the local aim keeps
            // only its pitch -- which is exactly what the upper body needs.
            state.aim_direction = new KernelVec3(0f, 3f, 4f);
            state.visual_flags = KernelConstants.VisualFlagGrounded |
                KernelConstants.VisualFlagFalling;
            state.animation_state = ushort.MaxValue;
            state.action = new KernelActionRuntimeView
            {
                phase = KernelActionPhase.Recovery,
            };

            applier.Apply(new[] { state }, 1);

            Assert.That(entityRegistry.TryGet(100, out GameObject visual), Is.True);
            NetworkActorView actorView = visual.GetComponent<NetworkActorView>();
            Assert.That(actorView.Speed, Is.EqualTo(5f).Within(0.0001f));
            Assert.That(actorView.IsGrounded, Is.True);
            Assert.That(actorView.IsFalling, Is.True);
            Assert.That(actorView.ActionPhase, Is.EqualTo(KernelActionPhase.Recovery));
            Assert.That(actorView.AimDirection.x, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(actorView.AimDirection.y, Is.EqualTo(0.6f).Within(0.0001f));
            Assert.That(actorView.AimDirection.z, Is.EqualTo(0.8f).Within(0.0001f));
        }

        /// <summary>
        /// Holding a weapon up is a pose the actor keeps with no action running, so
        /// the aim flag alone has to carry the upper body layer. Before this the
        /// layer only surfaced during a shot, which left the raise and lower
        /// animations with no window to play in.
        /// </summary>
        [Test]
        public void Apply_WhileAimingWithNoAction_AsksForTheUpperBodyLayer()
        {
            RenderEntityState state = State(
                100,
                KernelEntityType.Actor,
                new KernelVec3(),
                KernelActorType.Player);
            state.template_id = 1;
            state.visual_flags = KernelConstants.VisualFlagAiming;

            applier.Apply(new[] { state }, 1);

            Assert.That(entityRegistry.TryGet(100, out GameObject visual), Is.True);
            NetworkActorView actorView = visual.GetComponent<NetworkActorView>();
            Assert.That(actorView.IsAiming, Is.True);
            Assert.That(actorView.ActionPhase, Is.EqualTo(KernelActionPhase.None));
            Assert.That(actorView.ResolveUpperBodyLayerGoal(), Is.EqualTo(1f));
        }

        /// <summary>
        /// The backpedal signal. Facing is held on the aim while the feet go the
        /// other way, so the move vector taken into the actor's own frame points
        /// backwards -- which is the only thing that can tell the locomotion layer
        /// to reverse the cycle instead of running the forward one.
        /// </summary>
        [Test]
        public void Apply_WhileAimingAgainstTheDirectionOfTravel_ReportsBackwardLocalMove()
        {
            RenderEntityState state = State(
                100,
                KernelEntityType.Actor,
                new KernelVec3(),
                KernelActorType.Player);
            state.template_id = 1;
            state.visual_flags = KernelConstants.VisualFlagAiming |
                KernelConstants.VisualFlagMoving;
            state.aim_direction = new KernelVec3(0f, 0f, 1f);
            state.velocity = new KernelVec3(0f, 0f, -5f);

            applier.Apply(new[] { state }, 1);

            Assert.That(entityRegistry.TryGet(100, out GameObject visual), Is.True);
            NetworkActorView actorView = visual.GetComponent<NetworkActorView>();
            Assert.That(actorView.LocalMove.x, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(actorView.LocalMove.y, Is.EqualTo(-1f).Within(0.0001f));
        }

        /// <summary>
        /// The same world velocity with no aim turns the body round instead, so the
        /// actor is running forwards and the local move has to say so.
        /// </summary>
        [Test]
        public void Apply_WhileNotAiming_ReportsForwardLocalMoveForTheSameVelocity()
        {
            RenderEntityState state = State(
                100,
                KernelEntityType.Actor,
                new KernelVec3(),
                KernelActorType.Player);
            state.template_id = 1;
            state.visual_flags = KernelConstants.VisualFlagMoving;
            state.aim_direction = new KernelVec3(0f, 0f, 1f);
            state.velocity = new KernelVec3(0f, 0f, -5f);

            applier.Apply(new[] { state }, 1);

            Assert.That(entityRegistry.TryGet(100, out GameObject visual), Is.True);
            NetworkActorView actorView = visual.GetComponent<NetworkActorView>();
            Assert.That(actorView.LocalMove.x, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(actorView.LocalMove.y, Is.EqualTo(1f).Within(0.0001f));
        }

        /// <summary>
        /// Strafing while aimed. Facing is pinned to the aim, so travel across the
        /// body reads on the lateral axis -- and the sign has to match the actor's
        /// own right, not the world's. Facing +Z puts its right at +X.
        /// </summary>
        [Test]
        public void Apply_WhileAimingAcrossTheDirectionOfTravel_ReportsLateralLocalMove()
        {
            RenderEntityState state = State(
                100,
                KernelEntityType.Actor,
                new KernelVec3(),
                KernelActorType.Player);
            state.template_id = 1;
            state.visual_flags = KernelConstants.VisualFlagAiming |
                KernelConstants.VisualFlagMoving;
            state.aim_direction = new KernelVec3(0f, 0f, 1f);
            state.velocity = new KernelVec3(-5f, 0f, 0f);

            applier.Apply(new[] { state }, 1);

            Assert.That(entityRegistry.TryGet(100, out GameObject visual), Is.True);
            NetworkActorView actorView = visual.GetComponent<NetworkActorView>();
            Assert.That(actorView.LocalMove.x, Is.EqualTo(-1f).Within(0.0001f));
            Assert.That(actorView.LocalMove.y, Is.EqualTo(0f).Within(0.0001f));
        }

        /// <summary>
        /// Releasing aim while backpedalling turns the body about at a limited
        /// rate, so for a moment the velocity really is sideways relative to a body
        /// that has not finished coming round. Measuring that would flick the legs
        /// through a sidestep on the way; a body chasing its own velocity is
        /// running forwards by definition and has to report so.
        /// </summary>
        [Test]
        public void Apply_WhileFacingIsStillCatchingUpToVelocity_StaysOnForwardLocalMove()
        {
            RenderEntityState state = State(
                100,
                KernelEntityType.Actor,
                new KernelVec3(),
                KernelActorType.Player);
            state.template_id = 1;
            state.visual_flags = KernelConstants.VisualFlagMoving;
            state.velocity = new KernelVec3(0f, 0f, 5f);
            applier.Apply(new[] { state }, 1);

            // Same actor, velocity now square across the facing it just adopted.
            state.velocity = new KernelVec3(5f, 0f, 0f);
            applier.Apply(new[] { state }, 1);

            Assert.That(entityRegistry.TryGet(100, out GameObject visual), Is.True);
            NetworkActorView actorView = visual.GetComponent<NetworkActorView>();
            Assert.That(actorView.LocalMove.x, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(actorView.LocalMove.y, Is.EqualTo(1f).Within(0.0001f));
        }

        /// <summary>
        /// A thrown item is a gameplay request, not a weapon action: no
        /// VisualFlagFiring, no ActionPhase. None of the signals that normally
        /// raise the upper body layer fire, so the throw has to open a window of
        /// its own or it plays into a layer parked at zero and is never seen.
        /// </summary>
        [Test]
        public void TriggerItemThrow_RaisesTheUpperBodyLayerWithNoActionRunning()
        {
            NetworkActorView actorView = ApplyIdlePlayer();
            Assert.That(actorView.ResolveUpperBodyLayerGoal(), Is.EqualTo(0f));

            actorView.TriggerItemThrow();

            Assert.That(actorView.ActionPhase, Is.EqualTo(KernelActionPhase.None));
            Assert.That(actorView.IsFiring, Is.False);
            Assert.That(actorView.IsAiming, Is.False);
            Assert.That(actorView.ResolveUpperBodyLayerGoal(), Is.EqualTo(1f));
        }

        [Test]
        public void AdvanceItemThrowHold_PastTheWindow_ReleasesTheUpperBodyLayer()
        {
            NetworkActorView actorView = ApplyIdlePlayer();
            actorView.TriggerItemThrow();

            actorView.AdvanceItemThrowHold(0.5f);
            Assert.That(actorView.ResolveUpperBodyLayerGoal(), Is.EqualTo(1f));

            actorView.AdvanceItemThrowHold(5f);
            Assert.That(actorView.ResolveUpperBodyLayerGoal(), Is.EqualTo(0f));
        }

        [Test]
        public void TriggerItemThrow_OnADeadActor_DoesNothing()
        {
            RenderEntityState state = State(
                100,
                KernelEntityType.Actor,
                new KernelVec3(),
                KernelActorType.Player);
            state.template_id = 1;
            state.visual_flags = KernelConstants.VisualFlagDead;
            applier.Apply(new[] { state }, 1);
            Assert.That(entityRegistry.TryGet(100, out GameObject visual), Is.True);
            NetworkActorView actorView = visual.GetComponent<NetworkActorView>();

            actorView.TriggerItemThrow();

            Assert.That(actorView.ResolveUpperBodyLayerGoal(), Is.EqualTo(0f));
        }

        [Test]
        public void TriggerLocalItemThrow_WithAnUnknownNetId_IsIgnored()
        {
            ApplyIdlePlayer();

            Assert.DoesNotThrow(() => applier.TriggerLocalItemThrow(0U));
            Assert.DoesNotThrow(() => applier.TriggerLocalItemThrow(4242U));
        }

        [Test]
        public void TriggerLocalItemThrow_ReachesTheLocalPlayersActorView()
        {
            NetworkActorView actorView = ApplyIdlePlayer();

            applier.TriggerLocalItemThrow(100U);

            Assert.That(actorView.ResolveUpperBodyLayerGoal(), Is.EqualTo(1f));
        }

        private NetworkActorView ApplyIdlePlayer()
        {
            RenderEntityState state = State(
                100,
                KernelEntityType.Actor,
                new KernelVec3(),
                KernelActorType.Player);
            state.template_id = 1;
            applier.Apply(new[] { state }, 1);
            Assert.That(entityRegistry.TryGet(100, out GameObject visual), Is.True);
            return visual.GetComponent<NetworkActorView>();
        }

        [Test]
        public void Apply_WithNoVelocity_ReportsNoLocalMove()
        {
            RenderEntityState state = State(
                100,
                KernelEntityType.Actor,
                new KernelVec3(),
                KernelActorType.Player);
            state.template_id = 1;
            state.visual_flags = KernelConstants.VisualFlagAiming;
            state.aim_direction = new KernelVec3(0f, 0f, 1f);

            applier.Apply(new[] { state }, 1);

            Assert.That(entityRegistry.TryGet(100, out GameObject visual), Is.True);
            NetworkActorView actorView = visual.GetComponent<NetworkActorView>();
            Assert.That(actorView.LocalMove, Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void Apply_WhileNeitherAimingNorActing_ReleasesTheUpperBodyLayer()
        {
            RenderEntityState state = State(
                100,
                KernelEntityType.Actor,
                new KernelVec3(),
                KernelActorType.Player);
            state.template_id = 1;
            state.visual_flags = KernelConstants.VisualFlagMoving;

            applier.Apply(new[] { state }, 1);

            Assert.That(entityRegistry.TryGet(100, out GameObject visual), Is.True);
            NetworkActorView actorView = visual.GetComponent<NetworkActorView>();
            Assert.That(actorView.ResolveUpperBodyLayerGoal(), Is.EqualTo(0f));
        }

        [Test]
        public void Apply_WithStaleActor_KeepsLastPoseAndSuppressesPresentation()
        {
            RenderEntityState active = State(
                100,
                KernelEntityType.Actor,
                new KernelVec3(1f, 0f, 2f),
                KernelActorType.Player);
            active.template_id = 1;
            active.visual_flags = KernelConstants.VisualFlagMoving;
            applier.Apply(new[] { active }, 1);

            var stale = active;
            stale.status = RenderEntityStatus.Stale;
            stale.position = new KernelVec3(9f, 0f, 9f);
            stale.visual_flags = KernelConstants.VisualFlagDead;
            applier.Apply(new[] { stale }, 1);

            Assert.That(entityRegistry.TryGet(100, out GameObject visual), Is.True);
            NetworkActorView actorView = visual.GetComponent<NetworkActorView>();
            Assert.That(visual.transform.position, Is.EqualTo(new Vector3(1f, 0f, 2f)));
            Assert.That(actorView.IsStale, Is.True);
            Assert.That(actorView.IsMoving, Is.True);
            Assert.That(actorView.IsDead, Is.False);

            var remoteEvent = new KernelRemoteActionPresentationEvent
            {
                actor_net_id = 100,
                action_instance_id = 5,
                first_commit_index = 0,
                commit_count = 1,
                event_type = KernelRemoteActionPresentationEventType.FireCommit,
            };
            applier.ApplyRemoteActionPresentationEvents(new[] { remoteEvent }, 1);
            Assert.That(actorView.RemoteCommitCount, Is.Zero);

            var resumed = active;
            resumed.position = new KernelVec3(3f, 0f, 4f);
            applier.Apply(new[] { resumed }, 1);
            Assert.That(actorView.IsStale, Is.False);
            Assert.That(visual.transform.position, Is.EqualTo(new Vector3(3f, 0f, 4f)));
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
            var intent = new KernelActionIntent
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

        [Test]
        public void BeginPredictedLocalAction_DeduplicatesActionInstanceId()
        {
            GameObject visual = RegisterActorVisual(8, 102);
            var intent = new KernelActionIntent
            {
                action_instance_id = 91,
                binding_id = KernelActionBinding.PrimaryFire,
            };

            applier.BeginPredictedLocalAction(102, intent);
            applier.BeginPredictedLocalAction(102, intent);

            Assert.That(
                visual.GetComponent<NetworkActorView>().PredictedCommitCount,
                Is.EqualTo(1));
        }

        [Test]
        public void ApplyKernelEvents_DeduplicatesActorLandedByActorAndTick()
        {
            GameObject visual = RegisterActorVisual(8, 102);
            var landed = new KernelEvent
            {
                type = KernelEventType.ActorLanded,
                net_id = 102,
                tick = 45,
            };

            applier.ApplyKernelEvents(new[] { landed }, 1);
            applier.ApplyKernelEvents(new[] { landed }, 1);

            Assert.That(
                visual.GetComponent<NetworkActorView>().LandedCount,
                Is.EqualTo(1));
        }

        [Test]
        public void Apply_ServerBackedEntityPersistsUntilLifecycleEvent()
        {
            RenderEntityState actor = State(
                100,
                KernelEntityType.Actor,
                new KernelVec3(),
                KernelActorType.Player);
            actor.template_id = 1;
            applier.Apply(new[] { actor }, 1);

            applier.Apply(System.Array.Empty<RenderEntityState>(), 0);
            Assert.That(entityRegistry.TryGet(100, out _), Is.True);

            applier.ApplyEntityLifecycleEvents(
                new[]
                {
                    new KernelEntityLifecycleEvent
                    {
                        type = KernelEntityLifecycleEventType.Destroyed,
                        net_id = 100,
                        entity_type = KernelEntityType.Actor,
                    },
                },
                1);

            Assert.That(entityRegistry.TryGet(100, out _), Is.False);
        }

        [Test]
        public void Apply_PredictedProjectileMissingNextFrameIsRemoved()
        {
            var predicted = new RenderEntityState
            {
                entity_type = KernelEntityType.Projectile,
                template_id = 2,
                action_instance_id = 42,
                rotation = new KernelQuat(0f, 0f, 0f, 1f),
                status = RenderEntityStatus.Predicted,
            };
            applier.Apply(new[] { predicted }, 1);
            Assert.That(rootObject.transform.childCount, Is.EqualTo(1));

            applier.Apply(System.Array.Empty<RenderEntityState>(), 0);

            Assert.That(rootObject.transform.childCount, Is.Zero);
        }

        [Test]
        public void DefaultPrefabCatalog_BindsTracerArtForTheWeaponsThatSpawnNothing()
        {
            NetworkPrefabCatalog catalog = Resources.Load<NetworkPrefabCatalog>(
                NetworkPrefabRegistry.DefaultCatalogResourcePath);

            // rifle_shot and shotgun_shot. Nothing spawns from either -- both
            // weapons resolve by raycast -- but the template is where their art
            // is bound, and NetworkHitscanTracers looks for exactly this.
            Assert.That(
                catalog.TryGetProjectilePrefab(10, out GameObject rifleShot),
                Is.True,
                "rifle_shot has no tracer art");
            Assert.That(
                catalog.TryGetProjectilePrefab(11, out GameObject shotgunShot),
                Is.True,
                "shotgun_shot has no tracer art");
            Assert.That(rifleShot, Is.Not.SameAs(shotgunShot));

            // The tracer stretches one body mesh along its local +Z, so the art
            // has to be a root with exactly that child under it.
            foreach (GameObject art in new[] { rifleShot, shotgunShot })
            {
                Assert.That(art.GetComponent<NetworkTracerView>(), Is.Not.Null, art.name);
                Assert.That(art.transform.childCount, Is.EqualTo(1), art.name);
                Assert.That(
                    art.transform.GetChild(0).GetComponent<MeshRenderer>(),
                    Is.Not.Null,
                    art.name);
            }
        }

        [Test]
        public void InstantShot_DrawsWithTheArtBoundToItsProjectileTemplate()
        {
            NetworkHitscanTracers tracers = ConfigureRifleTracers();

            tracers.TryFire(RifleFireActionTemplateId, Vector3.zero, Vector3.forward);

            NetworkTracerView tracer =
                rootObject.GetComponentInChildren<NetworkTracerView>(true);
            Assert.That(tracer, Is.Not.Null);
            // The catalog's prefab rather than the procedural line, which is only
            // there for a template nothing is bound to.
            Material material =
                tracer.GetComponentInChildren<MeshRenderer>(true).sharedMaterial;
            Assert.That(material.name, Is.EqualTo("Projectile_RifleTracer"));
            // A material whose shader reference did not resolve still loads, under
            // the error shader, and renders magenta rather than reporting itself.
            Assert.That(
                material.shader.name,
                Is.EqualTo("Universal Render Pipeline/Unlit"));
        }

        [Test]
        public void HeldTriggerCommits_DrawOneInstantShotEach()
        {
            NetworkHitscanTracers tracers = ConfigureRifleTracers();
            NetworkActorView view = AimedActor(9, 103, RifleFireActionTemplateId);

            applier.BeginPredictedLocalAction(
                103,
                new KernelActionIntent
                {
                    action_instance_id = 5,
                    binding_id = KernelActionBinding.PrimaryFire,
                });
            applier.ApplyLocalActionResults(103, new[] { Committed(5, 1) }, 1);
            applier.ApplyLocalActionResults(103, new[] { Committed(5, 3) }, 1);

            // One press, three shots. A hold-fire weapon commits again and again
            // under the same action instance, and each result carries the running
            // total rather than the increment.
            Assert.That(view.PredictedCommitCount, Is.EqualTo(1));
            Assert.That(view.LocalCommitCount, Is.EqualTo(3));
            Assert.That(tracers.LiveTracerCount, Is.EqualTo(3));
        }

        [Test]
        public void RepeatedLocalActionResult_ConfirmsNoExtraShots()
        {
            NetworkHitscanTracers tracers = ConfigureRifleTracers();
            NetworkActorView view = AimedActor(9, 103, RifleFireActionTemplateId);
            applier.BeginPredictedLocalAction(
                103,
                new KernelActionIntent
                {
                    action_instance_id = 5,
                    binding_id = KernelActionBinding.PrimaryFire,
                });

            applier.ApplyLocalActionResults(103, new[] { Committed(5, 1) }, 1);
            applier.ApplyLocalActionResults(103, new[] { Committed(5, 1) }, 1);

            Assert.That(view.LocalCommitCount, Is.EqualTo(1));
            Assert.That(tracers.LiveTracerCount, Is.EqualTo(1));
        }

        [Test]
        public void RemoteFireCommit_DrawsOneInstantShotPerCommit()
        {
            NetworkHitscanTracers tracers = ConfigureRifleTracers();
            AimedActor(9, 103, RifleFireActionTemplateId);
            var remoteEvent = new KernelRemoteActionPresentationEvent
            {
                actor_net_id = 103,
                action_template_id = RifleFireActionTemplateId,
                action_instance_id = 12,
                first_commit_index = 0,
                commit_count = 2,
                event_type = KernelRemoteActionPresentationEventType.FireCommit,
            };

            applier.ApplyRemoteActionPresentationEvents(new[] { remoteEvent }, 1);
            applier.ApplyRemoteActionPresentationEvents(new[] { remoteEvent }, 1);

            // The second delivery is the same two commits, and a replayed commit
            // is not a second shot.
            Assert.That(tracers.LiveTracerCount, Is.EqualTo(2));
        }

        [Test]
        public void RemoteFireCommit_DrawsNothingForAWeaponThatSpawnsItsOwnShot()
        {
            NetworkHitscanTracers tracers = ConfigureRifleTracers();
            AimedActor(9, 103, RocketFireActionTemplateId);

            applier.ApplyRemoteActionPresentationEvents(
                new[]
                {
                    new KernelRemoteActionPresentationEvent
                    {
                        actor_net_id = 103,
                        action_template_id = RocketFireActionTemplateId,
                        action_instance_id = 12,
                        commit_count = 1,
                        event_type = KernelRemoteActionPresentationEventType.FireCommit,
                    },
                },
                1);

            // A rocket reaches the client as a projectile entity and is drawn from
            // its render state. Drawing a tracer for it too would double the shot.
            Assert.That(tracers.LiveTracerCount, Is.EqualTo(0));
        }

        [Test]
        public void InstantShot_PointsDownTheReplicatedAimFromTheMuzzle()
        {
            NetworkHitscanTracers tracers = ConfigureRifleTracers();
            AimedActor(9, 103, RifleFireActionTemplateId);

            tracers.TryFire(RifleFireActionTemplateId, Vector3.zero, Vector3.forward);

            NetworkTracerView tracer =
                rootObject.GetComponentInChildren<NetworkTracerView>(true);
            Assert.That(tracer, Is.Not.Null);
            Assert.That(tracer.IsPlaying, Is.True);
            // The kernel fires from position + (0, 1, 0).
            Assert.That(tracer.transform.position, Is.EqualTo(Vector3.up));
            Assert.That(
                Vector3.Angle(tracer.transform.forward, Vector3.forward),
                Is.LessThan(0.01f));
        }

        [Test]
        public void Tracer_ExpiresOnceItsDurationHasRun()
        {
            NetworkHitscanTracers tracers = ConfigureRifleTracers();
            AimedActor(9, 103, RifleFireActionTemplateId);
            tracers.TryFire(RifleFireActionTemplateId, Vector3.zero, Vector3.forward);

            tracers.Tick(1f);

            Assert.That(tracers.LiveTracerCount, Is.EqualTo(0));
        }

        [Test]
        public void ActorWhoseDeadFlagRises_ThrowsSplattersWhereItFell()
        {
            NetworkHitSplatters splatters = ConfigureSplatters();

            applier.Apply(new[] { Actor(105, alive: true) }, 1);
            Assert.That(splatters.LiveSplatCount, Is.Zero);

            applier.Apply(new[] { Actor(105, alive: false) }, 1);

            Assert.That(splatters.LiveSplatCount, Is.InRange(3, 5));
        }

        [Test]
        public void ActorThatStaysDead_ThrowsOneBurst()
        {
            NetworkHitSplatters splatters = ConfigureSplatters();

            applier.Apply(new[] { Actor(105, alive: true) }, 1);
            applier.Apply(new[] { Actor(105, alive: false) }, 1);
            int afterDeath = splatters.LiveSplatCount;

            // The replicated flag says "is dead", and stays true for as long as
            // the corpse is rendered. Only the edge is a death; treating the
            // level as one would repaint the ground every frame.
            for (int frame = 0; frame < 5; ++frame)
            {
                applier.Apply(new[] { Actor(105, alive: false) }, 1);
            }

            Assert.That(splatters.LiveSplatCount, Is.EqualTo(afterDeath));
        }

        [Test]
        public void ActorFirstSeenAlreadyDead_ThrowsNothing()
        {
            NetworkHitSplatters splatters = ConfigureSplatters();

            // What a late joiner sees, and what anyone sees when a corpse comes
            // into range. It died before this client was watching.
            applier.Apply(new[] { Actor(105, alive: false) }, 1);

            Assert.That(splatters.LiveSplatCount, Is.Zero);
        }

        [Test]
        public void DeathWithNoSplattersBound_IsHarmless()
        {
            // Splatters are optional wiring: a session without them still runs
            // every death, it just marks nothing.
            Assert.DoesNotThrow(() =>
            {
                applier.Apply(new[] { Actor(105, alive: true) }, 1);
                applier.Apply(new[] { Actor(105, alive: false) }, 1);
            });
        }

        [Test]
        public void ActorDestroyedWithoutEverLookingDead_ThrowsSplattersWhereItFell()
        {
            NetworkHitSplatters splatters = ConfigureSplatters();
            applier.Apply(new[] { Actor(105, alive: true) }, 1);

            // What this project actually does: an actor is removed on death and
            // never replicates a frame with the dead flag up, so the despawn is
            // the only notice the ground ever gets.
            applier.ApplyEntityLifecycleEvents(
                new[] { Despawn(105, KernelEntityType.Actor, KernelDespawnReason.Destroyed) },
                1);

            Assert.That(splatters.LiveSplatCount, Is.InRange(3, 5));
        }

        [Test]
        public void ActorLeavingRange_ThrowsNothing()
        {
            NetworkHitSplatters splatters = ConfigureSplatters();
            applier.Apply(new[] { Actor(105, alive: true) }, 1);

            applier.ApplyEntityLifecycleEvents(
                new[] { Despawn(105, KernelEntityType.Actor, KernelDespawnReason.OutOfRange) },
                1);

            Assert.That(splatters.LiveSplatCount, Is.Zero);
        }

        [Test]
        public void ProjectileDestroyed_ThrowsNothing()
        {
            NetworkHitSplatters splatters = ConfigureSplatters();
            applier.Apply(
                new[]
                {
                    State(106, KernelEntityType.Projectile, new KernelVec3(1f, 0f, 1f), KernelActorType.Unknown),
                },
                1);

            // Destroyed is what every spent projectile reports, and a firefight
            // is mostly spent projectiles. Without the entity-type filter each
            // one would mark the ground.
            applier.ApplyEntityLifecycleEvents(
                new[] { Despawn(106, KernelEntityType.Projectile, KernelDespawnReason.Destroyed) },
                1);

            Assert.That(splatters.LiveSplatCount, Is.Zero);
        }

        [Test]
        public void ActorAlreadyMarkedByItsDeadFlag_IsNotMarkedAgainOnDespawn()
        {
            NetworkHitSplatters splatters = ConfigureSplatters();
            applier.Apply(new[] { Actor(105, alive: true) }, 1);
            applier.Apply(new[] { Actor(105, alive: false) }, 1);
            int afterDeadFlag = splatters.LiveSplatCount;
            Assert.That(afterDeadFlag, Is.InRange(3, 5));

            applier.ApplyEntityLifecycleEvents(
                new[] { Despawn(105, KernelEntityType.Actor, KernelDespawnReason.Destroyed) },
                1);

            // Both signals describe one death. A server that replicates a dead
            // frame and then despawns must not double the mark.
            Assert.That(splatters.LiveSplatCount, Is.EqualTo(afterDeadFlag));
        }

        [Test]
        public void ActorFirstSeenDeadThenDespawned_IsStillNotMarked()
        {
            NetworkHitSplatters splatters = ConfigureSplatters();
            applier.Apply(new[] { Actor(105, alive: false) }, 1);

            applier.ApplyEntityLifecycleEvents(
                new[] { Despawn(105, KernelEntityType.Actor, KernelDespawnReason.Destroyed) },
                1);

            // It died before this client was watching. The despawn must not be
            // the back door that marks it anyway.
            Assert.That(splatters.LiveSplatCount, Is.Zero);
        }

        [Test]
        public void DeathPresentationEvent_NoLongerMarksTheGround()
        {
            NetworkHitSplatters splatters = ConfigureSplatters();
            RegisterActorVisual(11, 105);

            // The event belongs to an action instance and only fires when a
            // running action reports the death, so it drives the animator and
            // nothing else. The ground follows the replicated flag instead.
            applier.ApplyRemoteActionPresentationEvents(new[] { Death(105, 12) }, 1);

            Assert.That(splatters.LiveSplatCount, Is.Zero);
        }

        [Test]
        public void WildcardTriggerBinding_MatchesEveryActorAndEveryAction()
        {
            NetworkActorView view = ActorOfType(KernelActorType.Agent);
            view.ConfigureRemoteActionTriggers(new[]
            {
                Binding(
                    KernelActorType.Unknown,
                    0,
                    KernelRemoteActionPresentationEventType.DeathTrigger,
                    "Die"),
            });

            // One row for every weapon in the catalog and every actor type, which
            // is the whole reason the wildcards exist: what killed an actor does
            // not change how it falls over.
            Assert.That(
                view.ResolveRemoteActionTrigger(
                    Remote(
                        RifleFireActionTemplateId,
                        KernelRemoteActionPresentationEventType.DeathTrigger)),
                Is.EqualTo(Animator.StringToHash("Die")));
            Assert.That(
                view.ResolveRemoteActionTrigger(
                    Remote(
                        RocketFireActionTemplateId,
                        KernelRemoteActionPresentationEventType.DeathTrigger)),
                Is.EqualTo(Animator.StringToHash("Die")));
        }

        [Test]
        public void WildcardTriggerBinding_LeavesOtherEventsOnTheirDefaults()
        {
            NetworkActorView view = ActorOfType(KernelActorType.Agent);
            view.ConfigureRemoteActionTriggers(new[]
            {
                Binding(
                    KernelActorType.Unknown,
                    0,
                    KernelRemoteActionPresentationEventType.DeathTrigger,
                    "Die"),
            });

            // The event type is the row's subject, so a catch-all on one event
            // says nothing about any other.
            Assert.That(
                view.ResolveRemoteActionTrigger(
                    Remote(
                        RifleFireActionTemplateId,
                        KernelRemoteActionPresentationEventType.FireCommit)),
                Is.EqualTo(Animator.StringToHash("FireCommit")));
        }

        [Test]
        public void ExactTriggerBinding_BeatsAWildcardWhicheverIsAuthoredFirst()
        {
            RemoteActionTriggerBinding wildcard = Binding(
                KernelActorType.Unknown,
                0,
                KernelRemoteActionPresentationEventType.FireCommit,
                "GenericFire");
            RemoteActionTriggerBinding exact = Binding(
                KernelActorType.Player,
                RifleFireActionTemplateId,
                KernelRemoteActionPresentationEventType.FireCommit,
                "RifleFire");

            // Row order is not the tie-breaker, specificity is -- otherwise a
            // catch-all added later would silently swallow every weapon above it.
            foreach (RemoteActionTriggerBinding[] order in
                new[]
                {
                    new[] { wildcard, exact },
                    new[] { exact, wildcard },
                })
            {
                NetworkActorView view = ActorOfType(KernelActorType.Player);
                view.ConfigureRemoteActionTriggers(order);

                Assert.That(
                    view.ResolveRemoteActionTrigger(
                        Remote(
                            RifleFireActionTemplateId,
                            KernelRemoteActionPresentationEventType.FireCommit)),
                    Is.EqualTo(Animator.StringToHash("RifleFire")));
                Assert.That(
                    view.ResolveRemoteActionTrigger(
                        Remote(
                            RocketFireActionTemplateId,
                            KernelRemoteActionPresentationEventType.FireCommit)),
                    Is.EqualTo(Animator.StringToHash("GenericFire")));
            }
        }

        [Test]
        public void TriggerBindingForAnotherActorType_IsSkipped()
        {
            NetworkActorView view = ActorOfType(KernelActorType.Agent);
            view.ConfigureRemoteActionTriggers(new[]
            {
                Binding(
                    KernelActorType.Player,
                    0,
                    KernelRemoteActionPresentationEventType.DeathTrigger,
                    "PlayerDie"),
            });

            Assert.That(
                view.ResolveRemoteActionTrigger(
                    Remote(
                        RifleFireActionTemplateId,
                        KernelRemoteActionPresentationEventType.DeathTrigger)),
                Is.EqualTo(Animator.StringToHash("DeathTrigger")));
        }

        [Test]
        public void TriggerBindingWithNoAnimatorName_FallsBackToTheEventDefault()
        {
            NetworkActorView view = ActorOfType(KernelActorType.Player);
            view.ConfigureRemoteActionTriggers(new[]
            {
                Binding(
                    KernelActorType.Unknown,
                    0,
                    KernelRemoteActionPresentationEventType.DeathTrigger,
                    string.Empty),
            });

            // A half-authored row is the state an array spends most of its life
            // in while someone is filling it out in the inspector.
            Assert.That(
                view.ResolveRemoteActionTrigger(
                    Remote(
                        RifleFireActionTemplateId,
                        KernelRemoteActionPresentationEventType.DeathTrigger)),
                Is.EqualTo(Animator.StringToHash("DeathTrigger")));
        }

        [Test]
        public void NoTriggerBindings_FallBackToTheEventDefaults()
        {
            NetworkActorView view = ActorOfType(KernelActorType.Player);

            Assert.That(
                view.ResolveRemoteActionTrigger(
                    Remote(
                        RifleFireActionTemplateId,
                        KernelRemoteActionPresentationEventType.HitReaction)),
                Is.EqualTo(Animator.StringToHash("HitReaction")));
            Assert.That(
                view.ResolveRemoteActionTrigger(
                    Remote(
                        RifleFireActionTemplateId,
                        KernelRemoteActionPresentationEventType.DeathTrigger)),
                Is.EqualTo(Animator.StringToHash("DeathTrigger")));
        }

        private const uint RifleFireActionTemplateId = 4096;
        private const uint RocketFireActionTemplateId = 4099;

        private static RemoteActionTriggerBinding Binding(
            KernelActorType actorType,
            uint actionTemplateId,
            KernelRemoteActionPresentationEventType eventType,
            string animatorTrigger)
        {
            return new RemoteActionTriggerBinding(
                actorType,
                actionTemplateId,
                eventType,
                animatorTrigger);
        }

        private static KernelRemoteActionPresentationEvent Remote(
            uint actionTemplateId,
            KernelRemoteActionPresentationEventType eventType)
        {
            return new KernelRemoteActionPresentationEvent
            {
                actor_net_id = 200,
                action_template_id = actionTemplateId,
                action_instance_id = 1,
                first_commit_index = 0,
                commit_count = 1,
                event_type = eventType,
            };
        }

        private NetworkActorView ActorOfType(KernelActorType actorType)
        {
            GameObject visual = RegisterActorVisual(20, 200);
            NetworkActorView view = visual.GetComponent<NetworkActorView>();
            view.ApplyContinuousState(
                State(200, KernelEntityType.Actor, new KernelVec3(), actorType));
            return view;
        }

        private static KernelEntityLifecycleEvent Despawn(
            uint netId,
            KernelEntityType entityType,
            KernelDespawnReason reason)
        {
            return new KernelEntityLifecycleEvent
            {
                net_id = netId,
                entity_type = entityType,
                reason = reason,
            };
        }

        private static RenderEntityState Actor(uint netId, bool alive)
        {
            RenderEntityState state = State(
                netId,
                KernelEntityType.Actor,
                new KernelVec3(2f, 0f, -3f),
                KernelActorType.Player);
            state.visual_flags = alive ? 0u : KernelConstants.VisualFlagDead;
            return state;
        }

        private static KernelRemoteActionPresentationEvent Death(
            uint actorNetId,
            uint actionInstanceId)
        {
            return new KernelRemoteActionPresentationEvent
            {
                actor_net_id = actorNetId,
                action_instance_id = actionInstanceId,
                first_commit_index = 0,
                commit_count = 1,
                event_type = KernelRemoteActionPresentationEventType.DeathTrigger,
            };
        }

        /// <summary>
        /// Splatters plus the ground collider they probe for. Without a collider
        /// in the scene nothing lands, which is the effect's one scene dependency.
        /// </summary>
        private NetworkHitSplatters ConfigureSplatters()
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.SetParent(rootObject.transform);
            ground.transform.position = new Vector3(0f, -0.5f, 0f);
            ground.transform.localScale = new Vector3(200f, 1f, 200f);
            Physics.SyncTransforms();

            NetworkHitSplatters splatters =
                gameObject.AddComponent<NetworkHitSplatters>();
            splatters.Configure(rootObject.transform);
            applier.ConfigureSplatters(splatters);
            return splatters;
        }

        private NetworkHitscanTracers ConfigureRifleTracers()
        {
            NetworkHitscanTracers tracers =
                gameObject.AddComponent<NetworkHitscanTracers>();
            tracers.Configure(prefabRegistry, rootObject.transform);
            tracers.ConfigureWeapons(
                new Dictionary<uint, NetworkInstantWeaponPresentation>
                {
                    {
                        RifleFireActionTemplateId,
                        new NetworkInstantWeaponPresentation(
                            0,
                            RifleFireActionTemplateId,
                            10,
                            100f,
                            1,
                            0f)
                    },
                });
            applier.ConfigureTracers(tracers);
            return tracers;
        }

        /// <summary>
        /// An actor with a replicated aim and a running action, which is what a
        /// commit needs to become a ray.
        /// </summary>
        private NetworkActorView AimedActor(
            ulong entityId,
            uint netId,
            uint actionTemplateId)
        {
            GameObject visual = RegisterActorVisual(entityId, netId);
            NetworkActorView view = visual.GetComponent<NetworkActorView>();
            RenderEntityState state = State(
                netId,
                KernelEntityType.Actor,
                new KernelVec3(),
                KernelActorType.Player);
            state.aim_direction = new KernelVec3(0f, 0f, 1f);
            state.action = new KernelActionRuntimeView
            {
                action_template_id = actionTemplateId,
            };
            view.ApplyContinuousState(state);
            return view;
        }

        private static KernelLocalActionResult Committed(
            uint actionInstanceId,
            ushort confirmedCommitCount)
        {
            return new KernelLocalActionResult
            {
                action_instance_id = actionInstanceId,
                confirmed_commit_count = confirmedCommitCount,
                result = KernelLocalActionResultType.Accepted,
            };
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
