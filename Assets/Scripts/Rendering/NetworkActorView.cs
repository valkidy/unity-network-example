using System;
using System.Collections.Generic;
using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    [DisallowMultipleComponent]
    public sealed class NetworkActorView : MonoBehaviour
    {
        private const float MovementFacingSpeedThresholdSqr = 0.0001f;

        [Serializable]
        private sealed class LocalActionTriggerBinding
        {
            public KernelActionBinding binding;
            public string animatorTrigger;
        }

        [Serializable]
        private sealed class RemoteActionTriggerBinding
        {
            public KernelActorType actorType;
            public uint actionTemplateId;
            public KernelRemoteActionPresentationEventType eventType;
            public string animatorTrigger;
        }

        private static readonly int MovingParameter = Animator.StringToHash("Moving");
        private static readonly int ReloadingParameter = Animator.StringToHash("Reloading");
        private static readonly int DeadParameter = Animator.StringToHash("Dead");
        private static readonly int AimingParameter = Animator.StringToHash("Aiming");
        private static readonly int WindupParameter = Animator.StringToHash("Windup");
        private static readonly int FiringParameter = Animator.StringToHash("Firing");
        private static readonly int RecoveryParameter = Animator.StringToHash("Recovery");
        private static readonly int IdleParameter = Animator.StringToHash("Idle");
        private static readonly int FireCommitParameter = Animator.StringToHash("FireCommit");
        private static readonly int CastingCommitParameter = Animator.StringToHash("CastingCommit");
        private static readonly int ReloadCommitParameter = Animator.StringToHash("ReloadCommit");
        private static readonly int HitReactionParameter = Animator.StringToHash("HitReaction");
        private static readonly int DeathTriggerParameter = Animator.StringToHash("DeathTrigger");

        private Animator animator;
        private readonly HashSet<uint> predictedActionInstanceIds = new HashSet<uint>();
        private Quaternion lastMovementRotation;
        private bool hasMovementRotation;

        [SerializeField]
        private LocalActionTriggerBinding[] localActionTriggers;

        [SerializeField]
        private RemoteActionTriggerBinding[] remoteActionTriggers;

        public KernelActorType ActorType { get; private set; }
        public uint VisualFlags { get; private set; }
        public KernelActionPhase ActionPhase { get; private set; }
        public uint ActionInstanceId { get; private set; }
        public bool IsMoving { get; private set; }
        public bool IsReloading { get; private set; }
        public bool IsDead { get; private set; }
        public bool IsAiming { get; private set; }
        public bool IsWindup { get; private set; }
        public bool IsFiring { get; private set; }
        public bool IsRecovery { get; private set; }
        public bool IsIdle { get; private set; }
        public int PredictedCommitCount { get; private set; }
        public int RemoteCommitCount { get; private set; }

        public void ApplyContinuousState(RenderEntityState state)
        {
            ApplyMovementFacing(state);

            ActorType = state.actor_type;
            VisualFlags = state.visual_flags;
            ActionPhase = state.action.phase;
            ActionInstanceId = state.action.action_instance_id;

            IsDead = HasFlag(KernelConstants.VisualFlagDead);
            IsReloading = !IsDead && HasFlag(KernelConstants.VisualFlagReloading);
            IsMoving = !IsDead && HasFlag(KernelConstants.VisualFlagMoving);
            IsAiming = !IsDead && HasFlag(KernelConstants.VisualFlagAiming);
            IsWindup = !IsDead && state.action.phase == KernelActionPhase.Windup;
            IsFiring = !IsDead && !IsReloading &&
                (HasFlag(KernelConstants.VisualFlagFiring) ||
                    state.action.phase == KernelActionPhase.Active);
            IsRecovery = !IsDead && state.action.phase == KernelActionPhase.Recovery;
            IsIdle = !IsDead &&
                !IsMoving &&
                !IsReloading &&
                !IsAiming &&
                !IsWindup &&
                !IsFiring &&
                !IsRecovery &&
                state.action.phase == KernelActionPhase.None;

            Animator target = GetAnimator();
            SetBoolIfPresent(target, MovingParameter, IsMoving);
            SetBoolIfPresent(target, ReloadingParameter, IsReloading);
            SetBoolIfPresent(target, DeadParameter, IsDead);
            SetBoolIfPresent(target, AimingParameter, IsAiming);
            SetBoolIfPresent(target, WindupParameter, IsWindup);
            SetBoolIfPresent(target, FiringParameter, IsFiring);
            SetBoolIfPresent(target, RecoveryParameter, IsRecovery);
            SetBoolIfPresent(target, IdleParameter, IsIdle);
        }

        private void ApplyMovementFacing(RenderEntityState state)
        {
            Vector3 movement = new Vector3(
                state.velocity.x,
                0f,
                state.velocity.z);
            if (movement.sqrMagnitude > MovementFacingSpeedThresholdSqr)
            {
                lastMovementRotation = Quaternion.LookRotation(
                    movement.normalized,
                    Vector3.up);
                hasMovementRotation = true;
            }

            if (hasMovementRotation)
            {
                transform.rotation = lastMovementRotation;
            }
        }

        public void BeginPredictedAction(ActionIntent intent)
        {
            if (intent.action_instance_id == 0)
            {
                return;
            }

            predictedActionInstanceIds.Add(intent.action_instance_id);
            PredictedCommitCount++;
            int trigger = TriggerFor(intent.binding_id);
            SetTriggerIfPresent(GetAnimator(), trigger);
        }

        public void ApplyLocalActionResult(KernelLocalActionResult result)
        {
            if (result.action_instance_id == 0 ||
                !predictedActionInstanceIds.Remove(result.action_instance_id))
            {
                return;
            }

            // Accepted confirms the prediction without replaying its one-shot.
            // Corrected/rejected clear only presentation state; gameplay rollback
            // remains owned by the kernel and authoritative snapshots.
            if (result.result == KernelLocalActionResultType.Corrected ||
                result.result == KernelLocalActionResultType.Rejected)
            {
                SetBoolIfPresent(GetAnimator(), FiringParameter, false);
                SetBoolIfPresent(GetAnimator(), ReloadingParameter, false);
            }
        }

        public void PlayRemoteCommit(
            KernelRemoteActionPresentationEvent remoteEvent,
            uint commitIndex)
        {
            RemoteCommitCount++;
            SetTriggerIfPresent(GetAnimator(), TriggerFor(remoteEvent));
        }

        private bool HasFlag(uint flag)
        {
            return (VisualFlags & flag) != 0;
        }

        private Animator GetAnimator()
        {
            if (animator == null)
            {
                animator = GetComponentInChildren<Animator>();
            }
            return animator;
        }

        private int TriggerFor(KernelActionBinding binding)
        {
            if (localActionTriggers != null)
            {
                for (int index = 0; index < localActionTriggers.Length; ++index)
                {
                    LocalActionTriggerBinding candidate = localActionTriggers[index];
                    if (candidate != null &&
                        candidate.binding == binding &&
                        !string.IsNullOrEmpty(candidate.animatorTrigger))
                    {
                        return Animator.StringToHash(candidate.animatorTrigger);
                    }
                }
            }

            return binding == KernelActionBinding.Reload
                ? ReloadCommitParameter
                : FireCommitParameter;
        }

        private int TriggerFor(KernelRemoteActionPresentationEvent remoteEvent)
        {
            if (remoteActionTriggers != null)
            {
                for (int index = 0; index < remoteActionTriggers.Length; ++index)
                {
                    RemoteActionTriggerBinding candidate = remoteActionTriggers[index];
                    if (candidate != null &&
                        candidate.actorType == ActorType &&
                        candidate.actionTemplateId == remoteEvent.action_template_id &&
                        candidate.eventType == remoteEvent.event_type &&
                        !string.IsNullOrEmpty(candidate.animatorTrigger))
                    {
                        return Animator.StringToHash(candidate.animatorTrigger);
                    }
                }
            }

            return DefaultTriggerFor(remoteEvent.event_type);
        }

        private static int DefaultTriggerFor(
            KernelRemoteActionPresentationEventType eventType)
        {
            switch (eventType)
            {
                case KernelRemoteActionPresentationEventType.CastingCommit:
                    return CastingCommitParameter;
                case KernelRemoteActionPresentationEventType.ReloadCommit:
                    return ReloadCommitParameter;
                case KernelRemoteActionPresentationEventType.HitReaction:
                    return HitReactionParameter;
                case KernelRemoteActionPresentationEventType.DeathTrigger:
                    return DeathTriggerParameter;
                default:
                    return FireCommitParameter;
            }
        }

        private static void SetBoolIfPresent(Animator target, int parameter, bool value)
        {
            if (HasParameter(target, parameter, AnimatorControllerParameterType.Bool))
            {
                target.SetBool(parameter, value);
            }
        }

        private static void SetTriggerIfPresent(Animator target, int parameter)
        {
            if (HasParameter(target, parameter, AnimatorControllerParameterType.Trigger))
            {
                target.SetTrigger(parameter);
            }
        }

        private static bool HasParameter(
            Animator target,
            int parameter,
            AnimatorControllerParameterType type)
        {
            if (target == null || target.runtimeAnimatorController == null)
            {
                return false;
            }

            AnimatorControllerParameter[] parameters = target.parameters;
            for (int index = 0; index < parameters.Length; ++index)
            {
                if (parameters[index].nameHash == parameter &&
                    parameters[index].type == type)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
