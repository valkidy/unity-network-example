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
        public void ExplicitInputDoesNotPollDevicesAndHandoffPreservesSequenceAndActionIds()
        {
            SetKey(Key.Space);
            var fire = sampler.SampleExplicit(Vector2.right, Vector3.left, true, true, false);
            var release = sampler.SampleExplicit(Vector2.zero, Vector3.forward, false, false, false);
            Assert.That(release.input_seq, Is.EqualTo(fire.input_seq + 1));
            Assert.That(release.action_input.action_instance_id, Is.EqualTo(fire.action_intent.action_instance_id));
            Assert.That(release.action_input.held, Is.Zero);
            Assert.That(release.move.x, Is.Zero);
            Assert.That(release.buttons, Is.Zero);
            Assert.That(sampler.OutstandingActionCount, Is.EqualTo(1));
            var manual = sampler.Sample();
            Assert.That(manual.input_seq, Is.EqualTo(release.input_seq + 1));
            Assert.That(manual.action_intent.action_instance_id, Is.GreaterThan(fire.action_intent.action_instance_id));
        }

        [Test]
        public void ExplicitReloadReleasesFireAndIsEdgeTriggered()
        {
            var fire = sampler.SampleExplicit(Vector2.zero, Vector3.forward, true, true, false);
            var reload = sampler.SampleExplicit(Vector2.zero, Vector3.forward, true, false, true);
            Assert.That(reload.action_input.action_instance_id, Is.EqualTo(fire.action_intent.action_instance_id));
            Assert.That(reload.action_input.held, Is.Zero);
            Assert.That(reload.action_intent.binding_id, Is.EqualTo(KernelActionBinding.Reload));
            var held = sampler.SampleExplicit(Vector2.zero, Vector3.forward, true, false, true);
            Assert.That(held.action_intent.action_instance_id, Is.Zero);
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

        /// <summary>
        /// Taking damage mid-burst cancels the fire action the player is still
        /// holding the trigger for. The press is already over, so no rising edge
        /// is coming: the sampler has to ask for a new action itself or the
        /// weapon stays dead until the player lets go.
        /// </summary>
        [Test]
        public void Sample_WhenHeldFireIsCancelled_StartsANewActionWhileStillHeld()
        {
            SetKey(Key.Space);
            KernelPlayerInput first = sampler.Sample();

            sampler.ApplyActionResult(
                first.action_intent.action_instance_id,
                KernelLocalActionResultType.Rejected,
                KernelLocalActionResultReason.Cancelled);

            KernelPlayerInput restarted = sampler.Sample();

            Assert.That(restarted.action_intent.action_instance_id, Is.Not.Zero);
            Assert.That(restarted.action_intent.binding_id,
                Is.EqualTo(KernelActionBinding.PrimaryFire));
            Assert.That(restarted.action_intent.action_instance_id,
                Is.Not.EqualTo(first.action_intent.action_instance_id));
        }

        /// <summary>
        /// A correction after a rollback carries no reason of its own, and it is
        /// the shape the kernel reports when an authoritative snapshot disagrees
        /// with a prediction -- which is what arriving damage produces.
        /// </summary>
        [Test]
        public void Sample_WhenHeldFireIsCorrected_StartsANewActionWhileStillHeld()
        {
            SetKey(Key.Space);
            KernelPlayerInput first = sampler.Sample();

            sampler.ApplyActionResult(
                first.action_intent.action_instance_id,
                KernelLocalActionResultType.Corrected,
                KernelLocalActionResultReason.None);

            KernelPlayerInput restarted = sampler.Sample();

            Assert.That(restarted.action_intent.action_instance_id, Is.Not.Zero);
            Assert.That(restarted.action_input.action_instance_id, Is.Zero);
        }

        /// <summary>
        /// The restarted action is a normal held action: the sample after it goes
        /// back to continuing it rather than starting a third.
        /// </summary>
        [Test]
        public void Sample_AfterRestartingHeldFire_ContinuesTheNewAction()
        {
            SetKey(Key.Space);
            KernelPlayerInput first = sampler.Sample();
            sampler.ApplyActionResult(
                first.action_intent.action_instance_id,
                KernelLocalActionResultType.Rejected,
                KernelLocalActionResultReason.Cancelled);

            KernelPlayerInput restarted = sampler.Sample();
            KernelPlayerInput held = sampler.Sample();

            Assert.That(held.action_intent.action_instance_id, Is.Zero);
            Assert.That(held.action_input.action_instance_id,
                Is.EqualTo(restarted.action_intent.action_instance_id));
            Assert.That(held.action_input.held, Is.EqualTo(1));
        }

        /// <summary>
        /// A refusal that will answer the same way until something else changes
        /// must not restart, or the sampler would spin out one intent per sample
        /// for as long as the trigger is down.
        /// </summary>
        [TestCase(KernelLocalActionResultReason.NoAmmo)]
        [TestCase(KernelLocalActionResultReason.Reloading)]
        [TestCase(KernelLocalActionResultReason.Cooldown)]
        [TestCase(KernelLocalActionResultReason.Dead)]
        [TestCase(KernelLocalActionResultReason.Busy)]
        public void Sample_WhenHeldFireIsRefused_WaitsForAFreshPress(
            KernelLocalActionResultReason reason)
        {
            SetKey(Key.Space);
            KernelPlayerInput first = sampler.Sample();

            sampler.ApplyActionResult(
                first.action_intent.action_instance_id,
                KernelLocalActionResultType.Rejected,
                reason);

            KernelPlayerInput afterRefusal = sampler.Sample();

            Assert.That(afterRefusal.action_intent.action_instance_id, Is.Zero);
            Assert.That(afterRefusal.action_input.action_instance_id, Is.Zero);

            SetKey();
            sampler.Sample();
            SetKey(Key.Space);
            KernelPlayerInput pressedAgain = sampler.Sample();

            Assert.That(pressedAgain.action_intent.action_instance_id, Is.Not.Zero);
        }

        /// <summary>
        /// A kernel that cancels every fire action the instant it starts must not
        /// be asked forever; the hold gives up and waits for a fresh press.
        /// </summary>
        [Test]
        public void Sample_WhenEveryRestartIsCancelled_StopsRetryingWithinTheBudget()
        {
            SetKey(Key.Space);
            KernelPlayerInput current = sampler.Sample();

            int intentsSeen = 0;
            for (int attempt = 0; attempt < 12; ++attempt)
            {
                sampler.ApplyActionResult(
                    current.action_intent.action_instance_id,
                    KernelLocalActionResultType.Rejected,
                    KernelLocalActionResultReason.Cancelled);
                current = sampler.Sample();
                if (current.action_intent.action_instance_id != 0)
                {
                    ++intentsSeen;
                }
            }

            Assert.That(intentsSeen, Is.LessThanOrEqualTo(3));
            Assert.That(current.action_intent.action_instance_id, Is.Zero);
        }

        /// <summary>
        /// Confirmed commits mean the hold is working, so the restarts it took to
        /// get there are paid back and a later interruption can restart again.
        /// </summary>
        [Test]
        public void Sample_AfterAcceptedResult_RefillsTheRestartBudget()
        {
            SetKey(Key.Space);
            KernelPlayerInput current = sampler.Sample();

            for (int burst = 0; burst < 4; ++burst)
            {
                sampler.ApplyActionResult(
                    current.action_intent.action_instance_id,
                    KernelLocalActionResultType.Rejected,
                    KernelLocalActionResultReason.Cancelled);
                current = sampler.Sample();
                Assert.That(current.action_intent.action_instance_id, Is.Not.Zero,
                    "Restart " + burst + " should be allowed after progress.");

                sampler.ApplyActionResult(
                    current.action_intent.action_instance_id,
                    KernelLocalActionResultType.Accepted,
                    KernelLocalActionResultReason.None);
            }
        }

        /// <summary>
        /// An action that ends after the player already let go must not arm a
        /// restart that fires on their next, unrelated press.
        /// </summary>
        [Test]
        public void Sample_WhenFireIsCancelledAfterRelease_DoesNotRestartOnItsOwn()
        {
            SetKey(Key.Space);
            KernelPlayerInput first = sampler.Sample();

            SetKey();
            sampler.Sample();
            sampler.ApplyActionResult(
                first.action_intent.action_instance_id,
                KernelLocalActionResultType.Rejected,
                KernelLocalActionResultReason.Cancelled);

            KernelPlayerInput idle = sampler.Sample();

            Assert.That(idle.action_intent.action_instance_id, Is.Zero);
            Assert.That(idle.action_input.action_instance_id, Is.Zero);
        }

        /// <summary>
        /// An accepted result is bookkeeping only: it must not disturb the action
        /// the trigger is still holding.
        /// </summary>
        [Test]
        public void ApplyActionResult_WhenAccepted_KeepsHeldActionIdentity()
        {
            SetKey(Key.Space);
            KernelPlayerInput first = sampler.Sample();

            sampler.ApplyActionResult(
                first.action_intent.action_instance_id,
                KernelLocalActionResultType.Accepted,
                KernelLocalActionResultReason.None);

            KernelPlayerInput held = sampler.Sample();

            Assert.That(sampler.OutstandingActionCount, Is.Zero);
            Assert.That(held.action_input.action_instance_id,
                Is.EqualTo(first.action_intent.action_instance_id));
            Assert.That(held.action_input.held, Is.EqualTo(1));
        }

        /// <summary>
        /// A result for some other action -- a reload finishing, a stale id --
        /// must not touch the fire action the trigger is holding.
        /// </summary>
        [Test]
        public void ApplyActionResult_ForAnotherAction_LeavesHeldFireAlone()
        {
            SetKey(Key.Space);
            KernelPlayerInput first = sampler.Sample();

            sampler.ApplyActionResult(
                first.action_intent.action_instance_id + 1000,
                KernelLocalActionResultType.Rejected,
                KernelLocalActionResultReason.Cancelled);

            KernelPlayerInput held = sampler.Sample();

            Assert.That(held.action_intent.action_instance_id, Is.Zero);
            Assert.That(held.action_input.action_instance_id,
                Is.EqualTo(first.action_intent.action_instance_id));
        }

        [Test]
        public void CanRestartWhileHeld_SplitsInterruptionsFromRefusals()
        {
            Assert.That(
                NetworkInputSampler.CanRestartWhileHeld(
                    KernelLocalActionResultReason.Cancelled),
                Is.True);
            Assert.That(
                NetworkInputSampler.CanRestartWhileHeld(
                    KernelLocalActionResultReason.TimedOut),
                Is.True);
            Assert.That(
                NetworkInputSampler.CanRestartWhileHeld(
                    KernelLocalActionResultReason.NoAmmo),
                Is.False);
            Assert.That(
                NetworkInputSampler.CanRestartWhileHeld(
                    KernelLocalActionResultReason.Dead),
                Is.False);
        }

        [Test]
        public void CanRestartWhileHeld_WaitsOutStunsRatherThanAFreshPress()
        {
            Assert.That(
                NetworkInputSampler.CanRestartWhileHeld(
                    KernelLocalActionResultReason.Staggered),
                Is.True);
            Assert.That(
                NetworkInputSampler.CanRestartWhileHeld(
                    KernelLocalActionResultReason.KnockedBack),
                Is.True);
        }

        [Test]
        public void SampleExplicit_WhileStaggered_SendsNoActionAndNoMovement()
        {
            sampler.UpdateLocalActorState(staggered: true, grounded: true, 0.1f);

            KernelPlayerInput fire = sampler.SampleExplicit(
                Vector2.right, Vector3.forward, false, true, false);
            KernelPlayerInput reload = sampler.SampleExplicit(
                Vector2.right, Vector3.forward, false, false, true);

            Assert.That(sampler.IsActionBlocked, Is.True);
            Assert.That(fire.action_intent.action_instance_id, Is.Zero);
            Assert.That(reload.action_intent.action_instance_id, Is.Zero);
            Assert.That(fire.move.x, Is.Zero);
            Assert.That(fire.move.y, Is.Zero);
        }

        /// <summary>
        /// The trigger was pressed during the stagger and never let go of: the
        /// player means to fire, so the shot starts the moment the stagger ends.
        /// </summary>
        [Test]
        public void SampleExplicit_TriggerHeldThroughStagger_FiresWhenItLifts()
        {
            sampler.UpdateLocalActorState(staggered: true, grounded: true, 0.1f);
            sampler.SampleExplicit(Vector2.zero, Vector3.forward, false, true, false);

            sampler.UpdateLocalActorState(staggered: false, grounded: true, 0.1f);
            KernelPlayerInput resumed = sampler.SampleExplicit(
                Vector2.right, Vector3.forward, false, true, false);

            Assert.That(resumed.action_intent.action_instance_id, Is.Not.Zero);
            Assert.That(resumed.action_intent.binding_id,
                Is.EqualTo(KernelActionBinding.PrimaryFire));
            Assert.That(resumed.move.x, Is.EqualTo(1f));
        }

        /// <summary>
        /// The stagger interrupts the burst before the flag has reached the
        /// client. The refusal alone holds the restart back, so it is not spent
        /// against a server that is still saying no.
        /// </summary>
        [Test]
        public void SampleExplicit_HeldFireInterruptedByStagger_RestartsAfterTheStagger()
        {
            KernelPlayerInput first = sampler.SampleExplicit(
                Vector2.zero, Vector3.forward, false, true, false);
            sampler.ApplyActionResult(
                first.action_intent.action_instance_id,
                KernelLocalActionResultType.Corrected,
                KernelLocalActionResultReason.Staggered);

            KernelPlayerInput beforeFlag = sampler.SampleExplicit(
                Vector2.zero, Vector3.forward, false, true, false);
            sampler.UpdateLocalActorState(staggered: true, grounded: true, 0.3f);
            KernelPlayerInput duringStagger = sampler.SampleExplicit(
                Vector2.zero, Vector3.forward, false, true, false);
            sampler.UpdateLocalActorState(staggered: false, grounded: true, 0.1f);
            KernelPlayerInput afterStagger = sampler.SampleExplicit(
                Vector2.zero, Vector3.forward, false, true, false);

            Assert.That(beforeFlag.action_intent.action_instance_id, Is.Zero);
            Assert.That(duringStagger.action_intent.action_instance_id, Is.Zero);
            Assert.That(afterStagger.action_intent.action_instance_id, Is.Not.Zero);
            Assert.That(afterStagger.action_intent.action_instance_id,
                Is.Not.EqualTo(first.action_intent.action_instance_id));
        }

        [Test]
        public void SampleExplicit_ReleasedDuringStagger_DoesNotFireAfterIt()
        {
            sampler.UpdateLocalActorState(staggered: true, grounded: true, 0.1f);
            sampler.SampleExplicit(Vector2.zero, Vector3.forward, false, true, false);
            sampler.SampleExplicit(Vector2.zero, Vector3.forward, false, false, false);

            sampler.UpdateLocalActorState(staggered: false, grounded: true, 0.1f);
            KernelPlayerInput after = sampler.SampleExplicit(
                Vector2.zero, Vector3.forward, false, false, false);

            Assert.That(after.action_intent.action_instance_id, Is.Zero);
        }

        /// <summary>
        /// Nothing replicates a knockback, so the gate reads it from the refusal
        /// and ends it on the landing -- which, like the kernel's own lockout,
        /// needs the actor to have left the ground first. Movement is left
        /// alone: the server ignores it while the actor is thrown.
        /// </summary>
        [Test]
        public void KnockedBackRefusal_BlocksActionsUntilTheActorLands()
        {
            KernelPlayerInput first = sampler.SampleExplicit(
                Vector2.zero, Vector3.forward, false, true, false);
            sampler.ApplyActionResult(
                first.action_intent.action_instance_id,
                KernelLocalActionResultType.Rejected,
                KernelLocalActionResultReason.KnockedBack);

            // Still grounded in the render states: the snapshot showing the
            // actor in the air has not arrived yet.
            sampler.UpdateLocalActorState(staggered: false, grounded: true, 0.1f);
            Assert.That(sampler.IsActionBlocked, Is.True);
            sampler.UpdateLocalActorState(staggered: false, grounded: false, 0.5f);
            KernelPlayerInput airborne = sampler.SampleExplicit(
                Vector2.right, Vector3.forward, false, true, false);

            Assert.That(sampler.IsActionBlocked, Is.True);
            Assert.That(sampler.IsMovementFrozen, Is.False);
            Assert.That(airborne.action_intent.action_instance_id, Is.Zero);
            Assert.That(airborne.move.x, Is.EqualTo(1f));

            sampler.UpdateLocalActorState(staggered: false, grounded: true, 0.1f);
            KernelPlayerInput landed = sampler.SampleExplicit(
                Vector2.zero, Vector3.forward, false, true, false);

            Assert.That(sampler.IsActionBlocked, Is.False);
            Assert.That(landed.action_intent.action_instance_id, Is.Not.Zero);
        }

        [Test]
        public void KnockedBackRefusal_ThatNeverLeavesTheGround_EndsAfterTheGrace()
        {
            KernelPlayerInput first = sampler.SampleExplicit(
                Vector2.zero, Vector3.forward, false, true, false);
            sampler.ApplyActionResult(
                first.action_intent.action_instance_id,
                KernelLocalActionResultType.Rejected,
                KernelLocalActionResultReason.KnockedBack);

            sampler.UpdateLocalActorState(staggered: false, grounded: true, 0.1f);
            Assert.That(sampler.IsActionBlocked, Is.True);
            sampler.UpdateLocalActorState(
                staggered: false,
                grounded: true,
                NetworkInputSampler.ActionRefusalGraceSeconds);

            Assert.That(sampler.IsActionBlocked, Is.False);
        }

        [Test]
        public void KnockedBackRefusal_NeverSeenToLand_EndsAtTheLockoutLimit()
        {
            KernelPlayerInput first = sampler.SampleExplicit(
                Vector2.zero, Vector3.forward, false, true, false);
            sampler.ApplyActionResult(
                first.action_intent.action_instance_id,
                KernelLocalActionResultType.Rejected,
                KernelLocalActionResultReason.KnockedBack);

            float step = 0.5f;
            float elapsed = 0f;
            while (elapsed + step < NetworkInputSampler.KnockbackLockoutLimitSeconds)
            {
                sampler.UpdateLocalActorState(staggered: false, grounded: false, step);
                elapsed += step;
                Assert.That(sampler.IsActionBlocked, Is.True, "at " + elapsed + "s");
            }

            sampler.UpdateLocalActorState(staggered: false, grounded: false, step);

            Assert.That(sampler.IsActionBlocked, Is.False);
        }

        private void SetKey(params Key[] keys)
        {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));
            InputSystem.Update();
        }

    }
}
