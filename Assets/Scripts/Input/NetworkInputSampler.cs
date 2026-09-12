using System.Collections.Generic;
using NetworkExample.Kernel;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NetworkExample.UnityDemo.Input
{
    [DisallowMultipleComponent]
    public sealed class NetworkInputSampler : MonoBehaviour
    {
        private static readonly string[] WeaponSelectBindings =
        {
            "<Keyboard>/1",
            "<Keyboard>/2",
            "<Keyboard>/3",
            "<Keyboard>/4",
            "<Keyboard>/5",
            "<Keyboard>/6",
            "<Keyboard>/7",
        };

        [SerializeField]
        private float firePressedThreshold = 0.5f;

        /// <summary>
        /// Hold-to-aim by default. Toggle mode latches the state on each press
        /// instead, for players who would rather not hold a button down.
        /// </summary>
        [SerializeField]
        private bool aimToggleMode = false;

        private InputAction moveAction;
        private InputAction fireAction;
        private InputAction[] weaponSelectActions;
        private InputAction reloadAction;
        private InputAction aimAction;
        private Transform viewTransform;
        private Vector3 reticleAimDirection;
        private readonly HashSet<uint> outstandingActionIds = new HashSet<uint>();
        private uint inputSequence;
        private uint nextActionInstanceId = 1;
        private uint heldFireActionInstanceId;
        private readonly byte[] weaponIdsBySlot = new byte[KernelConstants.MaxWeaponSlots];
        private int weaponSlotCount;
        private int activeWeaponSlot;
        private int selectedWeaponSlot;
        // KernelPlayerInput.selected_weapon carries a catalog weapon ID, while number
        // keys select positions in the player's weapon_slots loadout.
        private byte selectedWeapon;
        private bool wasFirePressed;
        private bool wasReloadPressed;
        private bool wasAimPressed;
        private bool isAiming;

        public int OutstandingActionCount => outstandingActionIds.Count;

        /// <summary>
        /// The one aim state everything else reads -- the camera framing, the
        /// reticle and the <see cref="InputButton.Aim"/> bit the kernel sees are
        /// all driven from here, so they cannot disagree.
        /// </summary>
        public bool IsAiming => isAiming;
        public bool HasWeaponLoadout => weaponSlotCount > 0;
        public int SelectedWeaponSlot => selectedWeaponSlot;
        public byte SelectedWeaponId => selectedWeapon;

        public void SetViewTransform(Transform target)
        {
            viewTransform = target;
        }

        /// <summary>
        /// Supplies the direction the reticle is pointing at, pushed once a frame
        /// by the runner. It takes precedence over the view transform's forward:
        /// once the reticle is allowed to sit off centre, camera forward is no
        /// longer the direction a shot has to travel to land under it. A zero
        /// vector clears the override.
        /// </summary>
        public void SetAimDirection(Vector3 direction)
        {
            reticleAimDirection = direction;
        }

        public bool ConfigureWeaponLoadout(byte[] weaponIds, int initialActiveSlot)
        {
            if (weaponIds == null ||
                weaponIds.Length == 0 ||
                weaponIds.Length > KernelConstants.MaxWeaponSlots ||
                initialActiveSlot < 0 ||
                initialActiveSlot >= weaponIds.Length)
            {
                return false;
            }

            for (int index = 0; index < weaponIds.Length; ++index)
            {
                for (int previous = 0; previous < index; ++previous)
                {
                    if (weaponIds[previous] == weaponIds[index])
                    {
                        return false;
                    }
                }
            }

            System.Array.Clear(weaponIdsBySlot, 0, weaponIdsBySlot.Length);
            System.Array.Copy(weaponIds, weaponIdsBySlot, weaponIds.Length);
            weaponSlotCount = weaponIds.Length;
            activeWeaponSlot = initialActiveSlot;
            return TrySelectWeaponSlot(initialActiveSlot);
        }

        public bool TrySelectWeaponSlot(int slot)
        {
            if (slot < 0 || slot >= weaponSlotCount)
            {
                return false;
            }

            selectedWeaponSlot = slot;
            selectedWeapon = weaponIdsBySlot[slot];
            return true;
        }

        private void Awake()
        {
            EnsureActionsCreated();
        }

        private void EnsureActionsCreated()
        {
            if (moveAction != null)
            {
                return;
            }

            moveAction = new InputAction("Move", InputActionType.Value, expectedControlType: "Vector2");
            moveAction.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w")
                .With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a")
                .With("Right", "<Keyboard>/d");
            moveAction.AddBinding("<Gamepad>/leftStick");

            fireAction = new InputAction("Fire", InputActionType.Button);
            // fireAction.AddBinding("<Mouse>/leftButton");
            fireAction.AddBinding("<Keyboard>/space");
            fireAction.AddBinding("<Gamepad>/rightTrigger");

            weaponSelectActions = new InputAction[KernelConstants.MaxWeaponSlots];
            for (int index = 0; index < weaponSelectActions.Length; ++index)
            {
                InputAction selectAction = new InputAction(
                    "SelectWeapon" + index,
                    InputActionType.Button);
                if (index < WeaponSelectBindings.Length)
                {
                    selectAction.AddBinding(WeaponSelectBindings[index]);
                }

                weaponSelectActions[index] = selectAction;
            }

            reloadAction = new InputAction("Reload", InputActionType.Button);
            reloadAction.AddBinding("<Keyboard>/r");

            // Left shift keeps the arrow keys free to aim with and WASD free to
            // move with; the other two are the conventional aim controls on their
            // own devices.
            aimAction = new InputAction("Aim", InputActionType.Button);
            aimAction.AddBinding("<Keyboard>/leftShift");
            aimAction.AddBinding("<Mouse>/rightButton");
            aimAction.AddBinding("<Gamepad>/leftTrigger");
        }

        private void OnEnable()
        {
            moveAction?.Enable();
            fireAction?.Enable();
            SetWeaponSelectActionsEnabled(true);
            reloadAction?.Enable();
            aimAction?.Enable();
        }

        private void OnDisable()
        {
            moveAction?.Disable();
            fireAction?.Disable();
            SetWeaponSelectActionsEnabled(false);
            reloadAction?.Disable();
            aimAction?.Disable();
            ResetSession();
        }

        private void OnDestroy()
        {
            moveAction?.Dispose();
            moveAction = null;
            fireAction?.Dispose();
            fireAction = null;
            if (weaponSelectActions != null)
            {
                for (int index = 0; index < weaponSelectActions.Length; ++index)
                {
                    weaponSelectActions[index]?.Dispose();
                    weaponSelectActions[index] = null;
                }
            }
            weaponSelectActions = null;
            reloadAction?.Dispose();
            reloadAction = null;
            aimAction?.Dispose();
            aimAction = null;
        }

        /// <summary>
        /// Polls the aim button and folds it into <see cref="IsAiming"/>. Called
        /// once a frame by the runner so the camera can react at frame rate rather
        /// than at the slower input submission cadence, and again from
        /// <see cref="Sample"/> so a sample is never taken against a stale state.
        /// Edge detection makes the extra call a no-op.
        /// </summary>
        public void UpdateAimState()
        {
            EnsureActionsCreated();
            EnsureActionsEnabled();
            ApplyAimInput(IsActionPressed(aimAction));
        }

        /// <summary>
        /// Applies one already-read aim button state. Split from
        /// <see cref="UpdateAimState"/> so the hold and toggle behaviour can be
        /// driven directly without a device attached.
        /// </summary>
        public void ApplyAimInput(bool aimPressed)
        {
            if (aimToggleMode)
            {
                if (aimPressed && !wasAimPressed)
                {
                    isAiming = !isAiming;
                }
            }
            else
            {
                isAiming = aimPressed;
            }

            wasAimPressed = aimPressed;
        }

        public KernelPlayerInput Sample()
        {
            EnsureActionsCreated();
            EnsureActionsEnabled();
            UpdateAimState();
            Vector2 rawMove =
                moveAction == null ? Vector2.zero : moveAction.ReadValue<Vector2>();
            Vector2 move = TransformMoveToWorld(rawMove);

            UpdateSelectedWeapon();
            bool isFirePressed = IsFirePressed();
            bool fireTriggered = isFirePressed && !wasFirePressed;
            bool fireReleased = !isFirePressed && wasFirePressed;
            wasFirePressed = isFirePressed;
            bool isReloadPressed = IsActionPressed(reloadAction);
            bool reloadTriggered = isReloadPressed && !wasReloadPressed;
            wasReloadPressed = isReloadPressed;

            KernelActionIntent actionIntent = default;
            KernelActionInput actionInput = default;
            if (fireTriggered)
            {
                heldFireActionInstanceId = AllocateActionInstanceId();
                actionIntent = CreateActionIntent(
                    heldFireActionInstanceId,
                    KernelActionBinding.PrimaryFire);
            }
            else if (heldFireActionInstanceId != 0)
            {
                actionInput = new KernelActionInput
                {
                    action_instance_id = heldFireActionInstanceId,
                    held = isFirePressed ? (byte)1 : (byte)0,
                };
                if (fireReleased)
                {
                    heldFireActionInstanceId = 0;
                }
            }

            // KernelPlayerInput has one intent slot. When both actions begin on the same
            // sample, primary fire wins and reload can be attempted again later.
            if (reloadTriggered && actionIntent.action_instance_id == 0)
            {
                actionIntent = CreateActionIntent(
                    AllocateActionInstanceId(),
                    KernelActionBinding.Reload);
            }

            Vector3 aimDirection = GetAimDirection(move);

            inputSequence++;

            return new KernelPlayerInput
            {
                input_seq = inputSequence,
                client_action_time_us = NowMicroseconds(),
                // move = new KernelVec2(move.x * 0.1f, move.y * 0.1f), // Scale down movement input for better control at lower tick rates
                move = new KernelVec2(move.x, move.y),
                look_delta = new KernelVec2(0f, 0f),
                aim_dir = new KernelVec3(aimDirection.x, aimDirection.y, aimDirection.z),
                buttons = isAiming ? (uint)InputButton.Aim : 0U,
                selected_weapon = selectedWeapon,
                action_intent = actionIntent,
                action_input = actionInput,
            };
        }

        public Vector2 TransformMoveToWorld(Vector2 rawMove)
        {
            rawMove = Vector2.ClampMagnitude(rawMove, 1f);
            if (viewTransform == null)
            {
                return rawMove;
            }

            Vector3 forward = Vector3.ProjectOnPlane(viewTransform.forward, Vector3.up);
            Vector3 right = Vector3.ProjectOnPlane(viewTransform.right, Vector3.up);
            if (forward.sqrMagnitude <= 0.0001f || right.sqrMagnitude <= 0.0001f)
            {
                return rawMove;
            }

            forward.Normalize();
            right.Normalize();
            Vector3 worldMove = Vector3.ClampMagnitude(
                right * rawMove.x + forward * rawMove.y,
                1f);
            return new Vector2(worldMove.x, worldMove.z);
        }

        public Vector3 GetAimDirection(Vector2 worldMove)
        {
            if (reticleAimDirection.sqrMagnitude > 0.0001f)
            {
                return reticleAimDirection.normalized;
            }

            if (viewTransform != null && viewTransform.forward.sqrMagnitude > 0.0001f)
            {
                return viewTransform.forward.normalized;
            }

            return worldMove.sqrMagnitude > 0.0001f
                ? new Vector3(worldMove.x, 0f, worldMove.y).normalized
                : Vector3.forward;
        }

        public void CompleteAction(uint actionInstanceId)
        {
            if (actionInstanceId == 0)
            {
                return;
            }

            outstandingActionIds.Remove(actionInstanceId);
        }

        public void StopActionInput(uint actionInstanceId)
        {
            CompleteAction(actionInstanceId);
            if (heldFireActionInstanceId == actionInstanceId)
            {
                heldFireActionInstanceId = 0;
            }
        }

        public void ResetSession()
        {
            outstandingActionIds.Clear();
            inputSequence = 0;
            nextActionInstanceId = 1;
            heldFireActionInstanceId = 0;
            wasFirePressed = false;
            wasReloadPressed = false;
            wasAimPressed = false;
            isAiming = false;
            if (weaponSlotCount > 0)
            {
                TrySelectWeaponSlot(activeWeaponSlot);
            }
        }

        private uint AllocateActionInstanceId()
        {
            uint actionInstanceId;
            do
            {
                actionInstanceId = nextActionInstanceId++;
            }
            while (actionInstanceId == 0 ||
                actionInstanceId == heldFireActionInstanceId ||
                outstandingActionIds.Contains(actionInstanceId));

            outstandingActionIds.Add(actionInstanceId);
            return actionInstanceId;
        }

        private static KernelActionIntent CreateActionIntent(
            uint actionInstanceId,
            KernelActionBinding binding)
        {
            return new KernelActionIntent
            {
                action_instance_id = actionInstanceId,
                binding_id = binding,
            };
        }

        private bool IsFirePressed()
        {
            return IsActionPressed(fireAction);
        }

        private void UpdateSelectedWeapon()
        {
            if (weaponSelectActions == null)
            {
                return;
            }

            int selectableSlotCount = Mathf.Min(
                weaponSelectActions.Length,
                weaponSlotCount);
            for (int index = 0; index < selectableSlotCount; ++index)
            {
                if (IsActionPressed(weaponSelectActions[index]))
                {
                    TrySelectWeaponSlot(index);
                    return;
                }
            }
        }

        private bool IsActionPressed(InputAction action)
        {
            if (action == null)
            {
                return false;
            }

            return action.IsPressed() || action.ReadValue<float>() >= firePressedThreshold;
        }

        private void SetWeaponSelectActionsEnabled(bool enabled)
        {
            if (weaponSelectActions == null)
            {
                return;
            }

            for (int index = 0; index < weaponSelectActions.Length; ++index)
            {
                if (enabled)
                {
                    weaponSelectActions[index]?.Enable();
                }
                else
                {
                    weaponSelectActions[index]?.Disable();
                }
            }
        }

        private void EnsureActionsEnabled()
        {
            moveAction?.Enable();
            fireAction?.Enable();
            SetWeaponSelectActionsEnabled(true);
            reloadAction?.Enable();
            aimAction?.Enable();
        }

        private static ulong NowMicroseconds()
        {
            return (ulong)(Time.realtimeSinceStartupAsDouble * 1000000.0);
        }
    }
}
