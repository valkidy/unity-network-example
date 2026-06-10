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
        public void Sample_WithNoInput_DefaultsToWeaponSlotZero()
        {
            KernelPlayerInput input = sampler.Sample();

            Assert.That(input.selected_weapon, Is.EqualTo(0));
            Assert.That(input.client_action_id, Is.Zero);
        }

        [Test]
        public void Sample_WhenDigitOneIsPressed_SelectsWeaponSlotZero()
        {
            SetKey(Key.Digit1);

            KernelPlayerInput input = sampler.Sample();

            Assert.That(input.selected_weapon, Is.EqualTo(0));
            Assert.That(HasButton(input, InputButton.Fire), Is.False);
            Assert.That(input.client_action_id, Is.Zero);
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
        public void Sample_WhenDigitSevenIsPressed_SelectsWeaponSlotSix()
        {
            SetKey(Key.Digit7);

            KernelPlayerInput input = sampler.Sample();

            Assert.That(input.selected_weapon, Is.EqualTo(6));
            Assert.That(HasButton(input, InputButton.Fire), Is.False);
            Assert.That(input.client_action_id, Is.Zero);
        }

        [Test]
        public void Sample_AfterWeaponSelectionIsReleased_PreservesSelectedWeapon()
        {
            SetKey(Key.Digit7);
            sampler.Sample();

            SetKey();
            KernelPlayerInput input = sampler.Sample();

            Assert.That(input.selected_weapon, Is.EqualTo(6));
        }

        [Test]
        public void Sample_WhileFireButtonRemainsPressed_OnlyFirstSampleSubmitsFire()
        {
            SetKey(Key.Space);

            KernelPlayerInput first = sampler.Sample();
            KernelPlayerInput second = sampler.Sample();

            Assert.That(HasButton(first, InputButton.Fire), Is.True);
            Assert.That(first.selected_weapon, Is.EqualTo(0));
            Assert.That(first.client_action_id, Is.Not.Zero);
            Assert.That(HasButton(second, InputButton.Fire), Is.False);
            Assert.That(second.selected_weapon, Is.EqualTo(0));
            Assert.That(second.client_action_id, Is.Zero);
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

            Assert.That(HasButton(first, InputButton.Fire), Is.True);
            Assert.That(HasButton(released, InputButton.Fire), Is.False);
            Assert.That(HasButton(pressedAgain, InputButton.Fire), Is.True);
            Assert.That(pressedAgain.client_action_id, Is.Not.Zero);
            Assert.That(pressedAgain.client_action_id, Is.Not.EqualTo(first.client_action_id));
        }

        [Test]
        public void Sample_WhenReloadIsPressed_PreservesWeaponAndDoesNotCreateClientAction()
        {
            SetKey(Key.Digit7);
            sampler.Sample();

            SetKey(Key.R);
            KernelPlayerInput input = sampler.Sample();

            Assert.That(input.selected_weapon, Is.EqualTo(6));
            Assert.That(HasButton(input, InputButton.Reload), Is.True);
            Assert.That(input.client_action_id, Is.Zero);
        }

        [Test]
        public void Sample_WhenFireIsPressedForSlotZeroAndSlotSix_CreatesClientActions()
        {
            SetKey(Key.Space);
            KernelPlayerInput slotZeroFire = sampler.Sample();

            SetKey();
            sampler.Sample();

            SetKey(Key.Digit7);
            sampler.Sample();

            SetKey(Key.Space);
            KernelPlayerInput slotSixFire = sampler.Sample();

            Assert.That(slotZeroFire.selected_weapon, Is.EqualTo(0));
            Assert.That(slotZeroFire.client_action_id, Is.Not.Zero);
            Assert.That(slotSixFire.selected_weapon, Is.EqualTo(6));
            Assert.That(slotSixFire.client_action_id, Is.Not.Zero);
            Assert.That(slotSixFire.client_action_id, Is.Not.EqualTo(slotZeroFire.client_action_id));
        }

        private void SetKey(params Key[] keys)
        {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));
            InputSystem.Update();
        }

        private static bool HasButton(KernelPlayerInput input, InputButton button)
        {
            return (input.buttons & (uint)button) != 0;
        }
    }
}
