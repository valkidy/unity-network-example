using System.Reflection;
using NetworkExample.Kernel;
using NetworkExample.UnityDemo.Input;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class NetworkInputSamplerTests : InputTestFixture
    {
        private GameObject gameObject;
        private NetworkInputSampler sampler;
        private Keyboard keyboard;

        [SetUp]
        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            gameObject = new GameObject("NetworkInputSamplerTests");
            sampler = gameObject.AddComponent<NetworkInputSampler>();
            Assert.That(
                sampler.ConfigureWeaponLoadout(new byte[] { 3, 1, 7, 6 }, 0),
                Is.True);
            // EditMode does not invoke MonoBehaviour.OnEnable. Prime the sampler so
            // its programmatic InputActions are enabled before queuing device state.
            sampler.Sample();
        }

        [TearDown]
        public override void TearDown()
        {
            if (gameObject != null)
            {
                Object.DestroyImmediate(gameObject);
            }

            keyboard = null;
            base.TearDown();
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

        [Test]
        public void Sample_WithNoAimInput_LeavesButtonsClear()
        {
            KernelPlayerInput input = sampler.Sample();

            Assert.That(input.buttons, Is.Zero);
            Assert.That(sampler.IsAiming, Is.False);
        }

        [Test]
        public void Sample_WhileAimKeyIsHeld_SetsAimButtonBit()
        {
            SetKey(Key.LeftShift);

            KernelPlayerInput input = sampler.Sample();

            Assert.That(sampler.IsAiming, Is.True);
            Assert.That(input.buttons & (uint)InputButton.Aim, Is.EqualTo((uint)InputButton.Aim));
        }

        /// <summary>
        /// Hold-to-aim has to drop the moment the key comes up. Sample() re-polls
        /// the button itself, so a release that lands between submissions cannot
        /// leave the kernel holding a stale aim bit.
        /// </summary>
        [Test]
        public void Sample_AfterAimKeyIsReleased_ClearsAimButtonBit()
        {
            SetKey(Key.LeftShift);
            sampler.Sample();

            SetKey();
            KernelPlayerInput released = sampler.Sample();

            Assert.That(sampler.IsAiming, Is.False);
            Assert.That(released.buttons, Is.Zero);
        }

        [Test]
        public void ApplyAimInput_InHoldMode_TracksButtonState()
        {
            sampler.ApplyAimInput(true);
            Assert.That(sampler.IsAiming, Is.True);

            sampler.ApplyAimInput(true);
            Assert.That(sampler.IsAiming, Is.True);

            sampler.ApplyAimInput(false);
            Assert.That(sampler.IsAiming, Is.False);
        }

        [Test]
        public void ApplyAimInput_InToggleMode_LatchesOnEachPressEdge()
        {
            SetToggleMode(true);

            sampler.ApplyAimInput(true);
            Assert.That(sampler.IsAiming, Is.True);

            // Still held: no new edge, so the state must not flip back.
            sampler.ApplyAimInput(true);
            Assert.That(sampler.IsAiming, Is.True);

            sampler.ApplyAimInput(false);
            Assert.That(sampler.IsAiming, Is.True);

            sampler.ApplyAimInput(true);
            Assert.That(sampler.IsAiming, Is.False);
        }

        [Test]
        public void ResetSession_ClearsAimState()
        {
            sampler.ApplyAimInput(true);

            sampler.ResetSession();

            Assert.That(sampler.IsAiming, Is.False);
        }

        [Test]
        public void AimAction_BindsKeyboardMouseAndGamepad()
        {
            InputAction aim = GetAimAction();

            CollectionAssert.AreEquivalent(
                new[] { "<Keyboard>/leftShift", "<Mouse>/rightButton", "<Gamepad>/leftTrigger" },
                System.Linq.Enumerable.ToArray(
                    System.Linq.Enumerable.Select(aim.bindings, b => b.path)));
        }

        private InputAction GetAimAction()
        {
            FieldInfo field = typeof(NetworkInputSampler).GetField(
                "aimAction",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return (InputAction)field.GetValue(sampler);
        }

        private void SetToggleMode(bool enabled)
        {
            FieldInfo field = typeof(NetworkInputSampler).GetField(
                "aimToggleMode",
                BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(sampler, enabled);
        }

        [Test]
        public void GetAimDirection_WithPushedReticleDirection_PrefersItOverViewForward()
        {
            GameObject view = new GameObject("View");
            try
            {
                view.transform.rotation = Quaternion.identity;
                sampler.SetViewTransform(view.transform);
                sampler.SetAimDirection(new Vector3(1f, 0f, 1f));

                Vector3 aim = sampler.GetAimDirection(Vector2.zero);

                Assert.That(aim.x, Is.EqualTo(0.70710678f).Within(0.0001f));
                Assert.That(aim.z, Is.EqualTo(0.70710678f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(view);
            }
        }

        [Test]
        public void Sample_WithPushedReticleDirection_SubmitsThatAimDirection()
        {
            sampler.SetAimDirection(Vector3.right);

            KernelPlayerInput input = sampler.Sample();

            Assert.That(input.aim_dir.x, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(input.aim_dir.z, Is.EqualTo(0f).Within(0.0001f));
        }

        [Test]
        public void SetAimDirection_WithZeroVector_FallsBackToTheViewTransform()
        {
            GameObject view = new GameObject("View");
            try
            {
                view.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                sampler.SetViewTransform(view.transform);
                sampler.SetAimDirection(Vector3.zero);

                Vector3 aim = sampler.GetAimDirection(Vector2.zero);

                Assert.That(aim.x, Is.EqualTo(1f).Within(0.0001f));
                Assert.That(aim.z, Is.EqualTo(0f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(view);
            }
        }

        private void SetKey(params Key[] keys)
        {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));
            InputSystem.Update();
        }

    }
}
