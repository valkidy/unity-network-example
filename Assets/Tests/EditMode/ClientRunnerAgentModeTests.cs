using System.Reflection;
using NetworkExample.Kernel;
using NetworkExample.UnityDemo.Client;
using NetworkExample.UnityDemo.Input;
using NetworkExample.UnityDemo.LocalAgent;
using NetworkExample.UnityDemo.UI;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class ClientRunnerAgentModeTests
    {
        private GameObject root;
        private ClientRunner runner;
        private NetworkInputSampler sampler;
        private AimReticleView reticle;
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        [SetUp]
        public void Setup()
        {
            root = new GameObject("Agent mode integration");
            root.SetActive(false); // Test input arbitration without starting a network session.
            runner = root.AddComponent<ClientRunner>();
            sampler = root.AddComponent<NetworkInputSampler>();
            reticle = root.AddComponent<AimReticleView>();
            sampler.ConfigureWeaponLoadout(new byte[] { 3 }, 0);
            Set("inputSampler", sampler);
            Set("aimReticleView", reticle);
            Set("localAgentSettings", new LocalAgentSettings { enemyTemplateIds = new uint[] { 100 } });
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(root);

        private void Set(string name, object value) =>
            typeof(ClientRunner).GetField(name, PrivateInstance).SetValue(runner, value);
        private object Call(string name) => typeof(ClientRunner).GetMethod(name, PrivateInstance).Invoke(runner, null);

        [Test]
        public void AgentStartsANewFireActionForEveryPressOfAPressWeapon()
        {
            // Rocket (press mode) accepts a press and then ignores the held trigger,
            // so every shot the agent wants has to be a new action intent.
            var agent = new LocalAgentController();
            var settings = new LocalAgentSettings { enemyTemplateIds = new uint[] { 100 } };
            var weapon = new KernelLocalWeaponState { weapon_id = 3, ammo = 6,
                flags = KernelConstants.LocalWeaponStateFlagWeaponIdValid };
            var states = new[]
            {
                new RenderEntityState { net_id = 1, entity_type = KernelEntityType.Actor,
                    actor_type = KernelActorType.Player, hp = 100 },
                new RenderEntityState { net_id = 2, entity_type = KernelEntityType.Actor,
                    actor_type = KernelActorType.Agent, hp = 100, template_id = 100,
                    position = new KernelVec3(5, 0, 0) },
            };
            var intents = new System.Collections.Generic.HashSet<uint>();
            for (int i = 0; i < 6; i++)
            {
                LocalAgentCommand command = agent.Step(settings, states, 2, 1, true, weapon, 0f,
                    false, sampler.HeldFireActionInstanceId != 0);
                var input = sampler.SampleExplicit(command.Move, command.AimDirection,
                    command.Aim, command.Fire, command.Reload);
                if (input.action_intent.binding_id == KernelActionBinding.PrimaryFire &&
                    input.action_intent.action_instance_id != 0)
                    intents.Add(input.action_intent.action_instance_id);
                // The kernel accepts each press; no completion is ever reported.
                sampler.ApplyActionResult(input.action_intent.action_instance_id,
                    KernelLocalActionResultType.Accepted, KernelLocalActionResultReason.None);
            }
            Assert.That(intents.Count, Is.EqualTo(3));
        }

        [Test]
        public void AgentUsesTheRifleByDefaultAndGivesTheManualWeaponBack()
        {
            Assert.That(new LocalAgentSettings().preferredWeaponId, Is.EqualTo(0));
            sampler.ConfigureWeaponLoadout(new byte[] { 3, 1, 7, 0 }, 0);
            sampler.TrySelectWeaponSlot(1); // the player is holding the shotgun
            Set("localAgentSettings", new LocalAgentSettings());
            runner.EnableLocalAgent = true;
            Call("UpdateInputMode");
            Call("SelectAgentWeapon");
            Assert.That(sampler.SelectedWeaponId, Is.EqualTo(0));
            Assert.That(sampler.SelectedWeaponSlot, Is.EqualTo(3));
            runner.EnableLocalAgent = false;
            Call("UpdateInputMode");
            Assert.That(sampler.SelectedWeaponSlot, Is.EqualTo(1));
        }

        [Test]
        public void AgentKeepsTheCurrentWeaponWhenNoneIsPreferredOrItIsNotInTheLoadout()
        {
            sampler.ConfigureWeaponLoadout(new byte[] { 3, 1 }, 1);
            Set("localAgentSettings", new LocalAgentSettings { preferredWeaponId = -1 });
            Call("SelectAgentWeapon");
            Assert.That(sampler.SelectedWeaponId, Is.EqualTo(1));
            Set("localAgentSettings", new LocalAgentSettings()); // rifle, absent here
            Call("SelectAgentWeapon");
            Assert.That(sampler.SelectedWeaponId, Is.EqualTo(1));
        }

        private KernelLocalWeaponState Resolve(KernelLocalWeaponState weapon) =>
            (KernelLocalWeaponState)typeof(ClientRunner).GetMethod("ResolveAgentWeapon",
                BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { weapon, sampler });

        [Test]
        public void ClientWeaponStateIsResolvedThroughTheLoadout()
        {
            // What a client kernel reports: a slot, ammo, and no WeaponIdValid.
            sampler.ConfigureWeaponLoadout(new byte[] { 3, 1, 7, 0 }, 3);
            var weapon = Resolve(new KernelLocalWeaponState { active_weapon_slot = 3, ammo = 0, authoritative_ammo = 0 });
            Assert.That(weapon.weapon_id, Is.EqualTo(0));
            Assert.That(weapon.flags & KernelConstants.LocalWeaponStateFlagWeaponIdValid, Is.Not.Zero);
            Assert.That(weapon.ammo, Is.Zero, "the selected weapon's own magazine is kept");
        }

        [Test]
        public void SnapshotOfThePreviousWeaponIsNotReadAsTheSelectedWeaponsAmmo()
        {
            sampler.ConfigureWeaponLoadout(new byte[] { 3, 1, 7, 0 }, 0);
            sampler.TrySelectWeaponId(0); // agent picked the rifle; server still on the rocket
            var weapon = Resolve(new KernelLocalWeaponState { active_weapon_slot = 0, ammo = 0,
                flags = KernelConstants.LocalWeaponStateFlagReloading });
            Assert.That(weapon.weapon_id, Is.EqualTo(0));
            Assert.That(weapon.ammo, Is.GreaterThan(0));
            Assert.That(weapon.flags & KernelConstants.LocalWeaponStateFlagReloading, Is.Zero);
        }

        [Test]
        public void ToggleDefaultsOffAndBothDirectionsReleaseHeldFireWithoutResettingIds()
        {
            Assert.That(runner.EnableLocalAgent, Is.False);
            var manualFire = sampler.SampleExplicit(Vector2.right, Vector3.right, true, true, false);
            runner.EnableLocalAgent = true;
            Call("UpdateInputMode");
            Assert.That(reticle.Root.gameObject.activeSelf, Is.False);
            var entering = (KernelPlayerInput)Call("SampleCurrentInput");
            Assert.That(entering.action_input.action_instance_id, Is.EqualTo(manualFire.action_intent.action_instance_id));
            Assert.That(entering.action_input.held, Is.Zero);
            Assert.That(entering.move.x, Is.Zero);
            Assert.That(entering.input_seq, Is.EqualTo(manualFire.input_seq + 1));
            Set("pendingInputHandoff", false); // Represents successful TrySubmitInput.
            var agentFire = sampler.SampleExplicit(Vector2.zero, Vector3.left, true, true, false);
            runner.EnableLocalAgent = false;
            Call("UpdateInputMode");
            Assert.That(reticle.Root.gameObject.activeSelf, Is.True);
            var leaving = (KernelPlayerInput)Call("SampleCurrentInput");
            Assert.That(leaving.action_input.action_instance_id, Is.EqualTo(agentFire.action_intent.action_instance_id));
            Assert.That(leaving.action_input.held, Is.Zero);
            Assert.That(leaving.input_seq, Is.EqualTo(agentFire.input_seq + 1));
            Assert.That(agentFire.action_intent.action_instance_id, Is.GreaterThan(manualFire.action_intent.action_instance_id));
            Assert.That(sampler.OutstandingActionCount, Is.EqualTo(2));
        }
    }
}
