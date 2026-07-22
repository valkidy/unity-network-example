using NetworkExample.Kernel;
using NetworkExample.UnityDemo.Input;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using KernelPlayerInput = NetworkExample.Kernel.PlayerInput;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class NetworkInputSamplerTests
    {
        private GameObject gameObject;
        private NetworkInputSampler sampler;
        private Keyboard keyboard;

        [SetUp]
        public void SetUp()
        {
            keyboard = InputSystem.AddDevice<Keyboard>();
            gameObject = new GameObject("NetworkInputSamplerTests");
            sampler = gameObject.AddComponent<NetworkInputSampler>();
            Assert.That(
                sampler.ConfigureWeaponLoadout(new byte[] { 3, 1, 7, 6 }, 0),
                Is.True);
        }

        [TearDown]
        public void TearDown()
        {
            if (gameObject != null)
            {
                Object.DestroyImmediate(gameObject);
            }

            if (keyboard != null && keyboard.added)
            {
                InputSystem.RemoveDevice(keyboard);
            }
        }

        [Test]
        public void Sample_WithNoInput_UsesActiveSlotWeaponId()
        {
            KernelPlayerInput input = sampler.Sample();

            Assert.That(input.selected_weapon, Is.EqualTo(3));
            Assert.That(input.action_intent.action_instance_id, Is.Zero);
            Assert.That(input.action_input.action_instance_id, Is.Zero);
        }

        [Test]
        public void TrySelectWeaponSlot_MapsSlotToConfiguredWeaponId()
        {
            Assert.That(sampler.TrySelectWeaponSlot(2), Is.True);

            KernelPlayerInput input = sampler.Sample();

            Assert.That(sampler.SelectedWeaponSlot, Is.EqualTo(2));
            Assert.That(sampler.SelectedWeaponId, Is.EqualTo(7));
            Assert.That(input.selected_weapon, Is.EqualTo(7));
        }

        [Test]
        public void ConfigureWeaponLoadout_RejectsDuplicateWeaponIds()
        {
            Assert.That(
                sampler.ConfigureWeaponLoadout(new byte[] { 3, 3 }, 0),
                Is.False);
        }

        [Test]
        public void Sample_WhenDigitOneIsPressed_SelectsWeaponIdFromSlotZero()
        {
            SetKey(Key.Digit1);

            KernelPlayerInput input = sampler.Sample();

            Assert.That(input.selected_weapon, Is.EqualTo(3));
            Assert.That(input.action_intent.action_instance_id, Is.Zero);
        }

        [Test]
        public void Sample_WhenWIsPressed_SubmitsFullForwardMovement()
        {
            SetKey(Key.W);

            KernelPlayerInput input = sampler.Sample();

            Assert.That(input.move.x, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(input.move.y, Is.EqualTo(1f).Within(0.0001f));
        }

        [Test]
        public void Sample_WhenDigitFourIsPressed_SelectsWeaponIdFromSlotThree()
        {
            SetKey(Key.Digit4);

            KernelPlayerInput input = sampler.Sample();

            Assert.That(input.selected_weapon, Is.EqualTo(6));
            Assert.That(input.action_intent.action_instance_id, Is.Zero);
        }

        [Test]
        public void Sample_AfterWeaponSelectionIsReleased_PreservesSelectedWeapon()
        {
            SetKey(Key.Digit4);
            sampler.Sample();

            SetKey();
            KernelPlayerInput input = sampler.Sample();

            Assert.That(input.selected_weapon, Is.EqualTo(6));
        }

        [Test]
        public void Sample_WhileFireButtonRemainsPressed_ReusesActionIdForHeldInput()
        {
            SetKey(Key.Space);

            KernelPlayerInput first = sampler.Sample();
            KernelPlayerInput second = sampler.Sample();

            Assert.That(first.selected_weapon, Is.EqualTo(3));
            Assert.That(first.action_intent.binding_id, Is.EqualTo(KernelActionBinding.PrimaryFire));
            Assert.That(first.action_intent.action_instance_id, Is.Not.Zero);
            Assert.That(second.selected_weapon, Is.EqualTo(3));
            Assert.That(second.action_intent.action_instance_id, Is.Zero);
            Assert.That(second.action_input.action_instance_id,
                Is.EqualTo(first.action_intent.action_instance_id));
            Assert.That(second.action_input.held, Is.EqualTo(1));
        }

        [Test]
        public void Sample_AfterFireButtonIsReleased_FiresAgainOnNextPress()
        {
            SetKey(Key.Space);
            KernelPlayerInput first = sampler.Sample();

            SetKey();
            KernelPlayerInput released = sampler.Sample();

            SetKey(Key.Space);
            KernelPlayerInput pressedAgain = sampler.Sample();

            Assert.That(released.action_input.action_instance_id,
                Is.EqualTo(first.action_intent.action_instance_id));
            Assert.That(released.action_input.held, Is.Zero);
            Assert.That(pressedAgain.action_intent.action_instance_id, Is.Not.Zero);
            Assert.That(pressedAgain.action_intent.action_instance_id,
                Is.Not.EqualTo(first.action_intent.action_instance_id));
        }

        [Test]
        public void Sample_WhenReloadIsPressed_CreatesReloadActionIntent()
        {
            SetKey(Key.Digit4);
            sampler.Sample();

            SetKey(Key.R);
            KernelPlayerInput input = sampler.Sample();

            Assert.That(input.selected_weapon, Is.EqualTo(6));
            Assert.That(input.action_intent.binding_id, Is.EqualTo(KernelActionBinding.Reload));
            Assert.That(input.action_intent.action_instance_id, Is.Not.Zero);
        }

        [Test]
        public void Sample_WhenFireIsPressedForDifferentWeaponIds_CreatesClientActions()
        {
            SetKey(Key.Space);
            KernelPlayerInput slotZeroFire = sampler.Sample();

            SetKey();
            sampler.Sample();

            SetKey(Key.Digit4);
            sampler.Sample();

            SetKey(Key.Space);
            KernelPlayerInput slotThreeFire = sampler.Sample();

            Assert.That(slotZeroFire.selected_weapon, Is.EqualTo(3));
            Assert.That(slotZeroFire.action_intent.action_instance_id, Is.Not.Zero);
            Assert.That(slotThreeFire.selected_weapon, Is.EqualTo(6));
            Assert.That(slotThreeFire.action_intent.action_instance_id, Is.Not.Zero);
            Assert.That(slotThreeFire.action_intent.action_instance_id,
                Is.Not.EqualTo(slotZeroFire.action_intent.action_instance_id));
        }

        [Test]
        public void CompleteAction_ReleasesOutstandingActionBookkeeping()
        {
            SetKey(Key.Space);
            KernelPlayerInput input = sampler.Sample();

            Assert.That(sampler.OutstandingActionCount, Is.EqualTo(1));

            sampler.CompleteAction(input.action_intent.action_instance_id);

            Assert.That(sampler.OutstandingActionCount, Is.Zero);
        }

        [Test]
        public void CompleteAcceptedAction_DoesNotChangeHeldActionIdentity()
        {
            SetKey(Key.Space);
            KernelPlayerInput first = sampler.Sample();
            sampler.CompleteAction(first.action_intent.action_instance_id);

            KernelPlayerInput held = sampler.Sample();

            Assert.That(held.action_input.action_instance_id,
                Is.EqualTo(first.action_intent.action_instance_id));
            Assert.That(held.action_input.held, Is.EqualTo(1));
        }

        private void SetKey(params Key[] keys)
        {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));
            InputSystem.Update();
        }

    }
}
