using NetworkExample.Kernel;
using NetworkExample.UnityDemo.LocalAgent;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class LocalAgentControllerTests
    {
        private LocalAgentController controller;
        private LocalAgentPerception perception;
        private LocalAgentSettings settings;
        private KernelLocalWeaponState weapon;
        [SetUp]
        public void Setup()
        {
            controller = new LocalAgentController();
            perception = new LocalAgentPerception();
            settings = new LocalAgentSettings { enemyTemplateIds = new uint[] { 100 } };
            weapon = new KernelLocalWeaponState { weapon_id = 3, ammo = 10,
                flags = KernelConstants.LocalWeaponStateFlagWeaponIdValid };
        }
        private static RenderEntityState Actor(uint id, float x, KernelActorType type = KernelActorType.Player) =>
            new RenderEntityState { net_id = id, entity_type = KernelEntityType.Actor,
                actor_type = type, hp = 100, template_id = 100, position = new KernelVec3(x, 0, 0) };
        private LocalAgentCommand Step(params RenderEntityState[] states) =>
            StepWith(states, states.Length, true, weapon, 0f);
        // Omniscient perception: the controller sees exactly the render states.
        private LocalAgentCommand StepWith(RenderEntityState[] states, int count, bool hasWeapon,
            KernelLocalWeaponState weaponState, float age, bool holdTrigger = false, bool fireActionLive = false)
        {
            perception.Update(settings, 0f, states, count, 1);
            return controller.Step(settings, perception, hasWeapon, weaponState, age, holdTrigger, fireActionLive);
        }

        [Test]
        public void FollowLocksNearestWithStableTieAndReselectsWhenMissing()
        {
            Step(Actor(1, 0), Actor(3, 5), Actor(2, -5));
            Assert.That(controller.FollowTargetId, Is.EqualTo(2));
            Step(Actor(1, 0), Actor(3, 1), Actor(2, -5));
            Assert.That(controller.FollowTargetId, Is.EqualTo(2));
            Step(Actor(1, 0), Actor(3, 1));
            Assert.That(controller.FollowTargetId, Is.EqualTo(3));
        }

        [Test]
        public void FollowUsesHysteresisAndWorldPlane()
        {
            Assert.That(Step(Actor(1, 0), Actor(2, 4)).Move, Is.EqualTo(Vector2.zero));
            Assert.That(Step(Actor(1, 0), Actor(2, 5)).Move, Is.EqualTo(Vector2.right));
            Assert.That(Step(Actor(1, 0), Actor(2, 3.5f)).Move, Is.EqualTo(Vector2.right));
            Assert.That(Step(Actor(1, 0), Actor(2, 3)).Move, Is.EqualTo(Vector2.zero));
            Assert.That(Step(Actor(1, 0), Actor(2, 3.5f)).Move, Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void CombatStopsFollowingAndResumesAfterEnemyDisappears()
        {
            Step(Actor(1, 0), Actor(2, 10));
            var command = Step(Actor(1, 0), Actor(2, 10), Actor(3, -5, KernelActorType.Agent));
            Assert.That(command.Move, Is.EqualTo(Vector2.zero));
            Assert.That(command.Fire && command.Aim, Is.True);
            Assert.That(command.AimDirection, Is.EqualTo(Vector3.left));
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Combat));
            Assert.That(controller.FollowTargetId, Is.EqualTo(2));
            Assert.That(Step(Actor(1, 0), Actor(2, 10)).Move, Is.EqualTo(Vector2.right));
            Assert.That(controller.AttackTargetId, Is.Zero);
        }

        [Test]
        public void CombatWorksWithoutFollowerAndSelectsNearestWithStableTie()
        {
            Step(Actor(1, 0), Actor(4, 8, KernelActorType.Agent),
                Actor(3, 5, KernelActorType.Agent), Actor(2, -5, KernelActorType.Agent));
            Assert.That(controller.AttackTargetId, Is.EqualTo(2));
            Assert.That(controller.FollowTargetId, Is.Zero);
            Step(Actor(1, 0), Actor(4, 1, KernelActorType.Agent), Actor(2, -5, KernelActorType.Agent));
            Assert.That(controller.AttackTargetId, Is.EqualTo(4));
        }

        [Test]
        public void OnlyWhitelistedAgentsWithinThreeDimensionalRangeAreHostile()
        {
            var unknown = Actor(3, 1, KernelActorType.Agent); unknown.template_id = 999;
            var prop = Actor(4, 1, KernelActorType.Agent); prop.entity_type = KernelEntityType.Prop;
            var high = Actor(5, 1, KernelActorType.Agent); high.position.y = 20;
            Assert.That(Step(Actor(1, 0), Actor(2, 1), unknown, prop, high).Fire, Is.False);
            settings.enemyTemplateIds = new uint[0];
            Assert.That(Step(Actor(1, 0), Actor(3, 1, KernelActorType.Agent)).Fire, Is.False);
        }

        [Test]
        public void InvalidDeadAndStaleActorsAreIgnored()
        {
            var stale = Actor(2, 1); stale.status = RenderEntityStatus.Stale;
            var dead = Actor(3, 1); dead.visual_flags = KernelConstants.VisualFlagDead;
            var invalid = Actor(5, float.NaN);
            Step(Actor(1, 0), stale, dead, invalid, Actor(0, 1));
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Idle));
            Assert.That(controller.FollowTargetId, Is.Zero);
        }

        [Test]
        public void ActorsWhoseHealthIsNotReplicatedAreAliveUntilFlaggedDead()
        {
            // What a client really receives for an enemy: no hp, HpUnknown set.
            var enemy = Actor(2, 5, KernelActorType.Agent);
            enemy.hp = 0;
            enemy.visual_flags = KernelConstants.VisualFlagHpUnknown;
            Assert.That(Step(Actor(1, 0), enemy).Fire, Is.True);
            Assert.That(controller.AttackTargetId, Is.EqualTo(2));
            enemy.visual_flags |= KernelConstants.VisualFlagDead;
            Assert.That(Step(Actor(1, 0), enemy).Fire, Is.False);
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Idle));
        }

        [Test]
        public void LivenessReadsOnlyTheDeadFlagNeverHp()
        {
            // hp = 0 without the dead flag cannot come from a real snapshot, and hp
            // alone is not what decides it: the flag does, for players too.
            var zeroHp = Actor(2, 5); zeroHp.hp = 0;
            Step(Actor(1, 0), zeroHp);
            Assert.That(controller.FollowTargetId, Is.EqualTo(2));
            var deadWithHp = Actor(2, 5); deadWithHp.visual_flags = KernelConstants.VisualFlagDead;
            Step(Actor(1, 0), deadWithHp);
            Assert.That(controller.FollowTargetId, Is.Zero);
        }

        [Test]
        public void StaleFillInOfAnEnemyIsNotAttacked()
        {
            // The kernel's record for an entity missing from recent snapshots:
            // last known position, no hp, HpUnknown, and never the dead flag.
            var stale = Actor(2, 5, KernelActorType.Agent);
            stale.hp = 0;
            stale.visual_flags = KernelConstants.VisualFlagHpUnknown;
            stale.status = RenderEntityStatus.Stale;
            Assert.That(Step(Actor(1, 0), stale).Fire, Is.False);
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Idle));
        }

        [Test]
        public void MissingLocalOrExpiredObservationClearsTargets()
        {
            Step(Actor(1, 0), Actor(2, 5));
            Assert.That(Step(Actor(2, 5)).Move, Is.EqualTo(Vector2.zero));
            Assert.That(controller.FollowTargetId, Is.Zero);
            var states = new[] { Actor(1, 0), Actor(2, 5) };
            StepWith(states, 2, true, weapon, 0.6f);
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Idle));
        }

        [Test]
        public void ReloadIsOneShotUntilMeaningfulWeaponOrTargetChange()
        {
            weapon.ammo = 0;
            var states = new[] { Actor(1, 0), Actor(2, 5, KernelActorType.Agent) };
            var first = Step(states);
            Assert.That(first.Reload, Is.True);
            Assert.That(first.Fire, Is.False);
            weapon.authoritative_tick++;
            Assert.That(Step(states).Reload, Is.False);
            weapon.flags |= KernelConstants.LocalWeaponStateFlagReloading;
            Assert.That(Step(states).Reload, Is.False);
            weapon.flags = KernelConstants.LocalWeaponStateFlagWeaponIdValid;
            weapon.ammo = 10;
            Assert.That(Step(states).Fire, Is.True);
            weapon.ammo = 0;
            Assert.That(Step(states).Reload, Is.True);
            Assert.That(Step(Actor(1, 0), Actor(3, 5, KernelActorType.Agent)).Reload, Is.False);
            Assert.That(Step(Actor(1, 0), Actor(3, 5, KernelActorType.Agent)).Reload, Is.True);
        }

        [Test]
        public void PressWeaponReleasesTheTriggerBetweenShots()
        {
            var states = new[] { Actor(1, 0), Actor(2, 5, KernelActorType.Agent) };
            bool[] fire = new bool[5];
            for (int i = 0; i < fire.Length; i++)
                fire[i] = StepWith(states, 2, true, weapon, 0f, false, true).Fire;
            Assert.That(fire, Is.EqualTo(new[] { true, false, true, false, true }));
        }

        [Test]
        public void HoldWeaponKeepsHoldingUntilItsActionIsDropped()
        {
            var states = new[] { Actor(1, 0), Actor(2, 5, KernelActorType.Agent) };
            LocalAgentCommand Hold(bool live) =>
                StepWith(states, 2, true, weapon, 0f, true, live);
            Assert.That(Hold(false).Fire, Is.True);
            Assert.That(Hold(true).Fire, Is.True);
            Assert.That(Hold(true).Fire, Is.True);
            Assert.That(Hold(false).Fire, Is.False, "a refused hold needs a fresh press");
            Assert.That(Hold(false).Fire, Is.True);
        }

        [Test]
        public void TriggerStateDoesNotCarryAcrossLeavingCombat()
        {
            var enemy = new[] { Actor(1, 0), Actor(2, 5, KernelActorType.Agent) };
            Assert.That(Step(enemy).Fire, Is.True);
            Assert.That(Step(Actor(1, 0)).Fire, Is.False);
            Assert.That(Step(enemy).Fire, Is.True);
            weapon.ammo = 0;
            Assert.That(Step(enemy).Reload, Is.True);
            weapon.ammo = 10;
            Assert.That(Step(enemy).Fire, Is.True, "no press was pending while reloading");
        }

        [Test]
        public void LastAimDirectionKeepsTheLastNonZeroAim()
        {
            Assert.That(controller.LastAimDirection, Is.EqualTo(Vector3.forward));
            Step(Actor(1, 0), Actor(3, -5, KernelActorType.Agent));
            Assert.That(controller.LastAimDirection, Is.EqualTo(Vector3.left));
            Assert.That(Step(Actor(2, 5)).AimDirection, Is.EqualTo(Vector3.zero), "no self: default command");
            Assert.That(controller.LastAimDirection, Is.EqualTo(Vector3.left));
        }

        [Test]
        public void LimitedPerceptionShootsOnlyAnEnemyItHasSeenLongEnough()
        {
            settings.perceptionMode = PerceptionMode.Limited;
            settings.perceptionHz = 0f;
            LocalAgentCommand At(float time, params RenderEntityState[] states)
            {
                perception.Update(settings, time, states, states.Length, 1, controller.LastAimDirection);
                return controller.Step(settings, perception, true, weapon, 0f);
            }
            Assert.That(controller.LastAimDirection, Is.EqualTo(Vector3.forward), "facing +z");
            var behind = Actor(3, 0, KernelActorType.Agent); behind.position = new KernelVec3(0, 0, -5);
            Assert.That(At(0f, Actor(1, 0), behind).Fire, Is.False);
            Assert.That(controller.State, Is.Not.EqualTo(LocalAgentState.Combat), "the enemy behind is not seen");

            var ahead = Actor(3, 0, KernelActorType.Agent); ahead.position = new KernelVec3(0, 0, 5);
            Assert.That(At(1f, Actor(1, 0), ahead).Fire, Is.False, "seen, not yet reacted to");
            Assert.That(At(1.1f, Actor(1, 0), ahead).Fire, Is.False);
            var command = At(1.3f, Actor(1, 0), ahead);
            Assert.That(command.Fire, Is.True);
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Combat));
            Assert.That(command.AimDirection, Is.EqualTo(Vector3.forward));

            // Out of sight it is remembered, but not shot at.
            Assert.That(At(1.5f, Actor(1, 0), behind).Fire, Is.False);
            Assert.That(perception.Actors[0].Confirmed && !perception.Actors[0].VisibleNow, Is.True);
        }

        [Test]
        public void ARememberedEnemyIsLookedForWhereItWasAndShotOnceSeenAgain()
        {
            settings.perceptionMode = PerceptionMode.Limited;
            settings.perceptionHz = 0f;
            settings.reactionSeconds = 0f;
            float time = 0f;
            LocalAgentCommand Tick(params RenderEntityState[] states)
            {
                perception.Update(settings, time, states, states.Length, 1, controller.LastAimDirection);
                time += 0.1f;
                return controller.Step(settings, perception, true, weapon, 0f);
            }
            var enemy = Actor(3, 0, KernelActorType.Agent); enemy.position = new KernelVec3(0, 0, 10);
            Assert.That(Tick(Actor(1, 0), enemy).Fire, Is.True);

            // Out of the sync: walk straight at where it was (no navmesh), looking there.
            var search = Tick(Actor(1, 0));
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Investigating));
            Assert.That(controller.InvestigateTargetId, Is.EqualTo(3));
            Assert.That(controller.AttackTargetId, Is.Zero);
            Assert.That(search.Move, Is.EqualTo(Vector2.up));
            Assert.That(search.AimDirection, Is.EqualTo(Vector3.forward));
            Assert.That(search.Fire || search.Aim, Is.False);

            // Back in sight: fight again.
            Assert.That(Tick(Actor(1, 0), enemy).Fire, Is.True);
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Combat));
            Assert.That(controller.InvestigateTargetId, Is.Zero);

            // Out of range, dead or unknown enemies are not looked for.
            var far = Actor(4, 0, KernelActorType.Agent); far.position = new KernelVec3(0, 0, 20);
            controller.Reset();
            perception.Reset();
            Tick(Actor(1, 0), far);
            Tick(Actor(1, 0));
            Assert.That(controller.State, Is.Not.EqualTo(LocalAgentState.Investigating), "beyond threatRange");
        }

        [Test]
        public void TheLookAroundStartsWhereTheAgentArrivesAndEndsAfterAFullTurn()
        {
            settings.perceptionMode = PerceptionMode.Limited;
            settings.perceptionHz = 0f;
            settings.reactionSeconds = 0f;
            settings.investigateScanSeconds = 1f;
            float time = 0f;
            var self = Actor(1, 0);
            LocalAgentCommand Tick(params RenderEntityState[] others)
            {
                var states = new RenderEntityState[others.Length + 1];
                states[0] = self;
                others.CopyTo(states, 1);
                perception.Update(settings, time, states, states.Length, 1, controller.LastAimDirection);
                time += 0.25f;
                return controller.Step(settings, perception, true, weapon, 0f);
            }
            var enemy = Actor(3, 0, KernelActorType.Agent); enemy.position = new KernelVec3(0, 0, 10);
            Tick(enemy);
            self.position = new KernelVec3(0, 0, 9.5f); // it walked there
            var first = Tick();
            Assert.That(first.Move, Is.EqualTo(Vector2.zero), "within reach: look around");
            Assert.That(first.AimDirection, Is.EqualTo(Vector3.forward), "from where it was facing");
            Assert.That(Vector3.Angle(Tick().AimDirection, Vector3.right), Is.LessThan(0.01f), "a quarter turn a quarter second later");
            Assert.That(Vector3.Angle(Tick().AimDirection, Vector3.back), Is.LessThan(0.01f));
            Assert.That(Vector3.Angle(Tick().AimDirection, Vector3.left), Is.LessThan(0.01f));
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Investigating));
            Tick();
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Idle), "done: no one to follow, no navmesh");
            Tick();
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Idle), "the same memory is not searched again");

            // Seen again later, it is worth another look.
            Tick(enemy);
            Tick();
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Investigating));
        }

        [Test]
        public void UnknownWeaponDoesNotFireOrReloadAndCountBoundsAreRespected()
        {
            var states = new[] { Actor(1, 0), Actor(2, 5, KernelActorType.Agent) };
            var command = StepWith(states, 99, false, default, 0);
            Assert.That(command.Fire || command.Reload, Is.False);
            StepWith(states, 1, true, weapon, 0);
            Assert.That(controller.AttackTargetId, Is.Zero);
        }
    }
}
