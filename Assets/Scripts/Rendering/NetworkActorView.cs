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

        /// <summary>
        /// Sends one kind of replicated action event to one Animator trigger.
        /// </summary>
        /// <remarks>
        /// Both selectors carry a wildcard, and both spell it zero, because
        /// neither zero is a real value: an actor is never
        /// <see cref="KernelActorType.Unknown"/>, and the catalog numbers its
        /// action templates from 4096 up. That matters most for the events where
        /// the action is beside the point -- an actor dies the same way whatever
        /// killed it -- which would otherwise need one row per weapon per actor
        /// type to say one thing.
        ///
        /// The most specific row wins rather than the first, so a rifle's own
        /// fire animation can sit beside a catch-all without either having to be
        /// ordered around the other.
        /// </remarks>
        [Serializable]
        public sealed class RemoteActionTriggerBinding
        {
            [Tooltip("Actor this row applies to, or Unknown for every actor.")]
            public KernelActorType actorType;

            [Tooltip(
                "Catalog action template this row applies to, or 0 for every " +
                "action. Weapon templates are 4096 and up.")]
            public uint actionTemplateId;

            [Tooltip("The replicated event that fires the trigger.")]
            public KernelRemoteActionPresentationEventType eventType;

            [Tooltip(
                "Animator trigger to set. An empty name disables the row, and a " +
                "name the Animator does not declare is skipped rather than logged.")]
            public string animatorTrigger;

            public RemoteActionTriggerBinding()
            {
            }

            public RemoteActionTriggerBinding(
                KernelActorType actorType,
                uint actionTemplateId,
                KernelRemoteActionPresentationEventType eventType,
                string animatorTrigger)
            {
                this.actorType = actorType;
                this.actionTemplateId = actionTemplateId;
                this.eventType = eventType;
                this.animatorTrigger = animatorTrigger;
            }
        }

        private static readonly int MovingParameter = Animator.StringToHash("Moving");
        private static readonly int SpeedParameter = Animator.StringToHash("Speed");
        private static readonly int MoveXParameter = Animator.StringToHash("MoveX");
        private static readonly int MoveYParameter = Animator.StringToHash("MoveY");
        private static readonly int GroundedParameter = Animator.StringToHash("Grounded");
        private static readonly int FallingParameter = Animator.StringToHash("Falling");
        private static readonly int ReloadingParameter = Animator.StringToHash("Reloading");
        private static readonly int DeadParameter = Animator.StringToHash("Dead");
        private static readonly int AimingParameter = Animator.StringToHash("Aiming");
        private static readonly int ActionPhaseParameter = Animator.StringToHash("ActionPhase");
        private static readonly int AimXParameter = Animator.StringToHash("AimX");
        private static readonly int AimYParameter = Animator.StringToHash("AimY");
        private static readonly int AimZParameter = Animator.StringToHash("AimZ");
        private static readonly int WindupParameter = Animator.StringToHash("Windup");
        private static readonly int FiringParameter = Animator.StringToHash("Firing");
        private static readonly int RecoveryParameter = Animator.StringToHash("Recovery");
        private static readonly int IdleParameter = Animator.StringToHash("Idle");
        private static readonly int FireCommitParameter = Animator.StringToHash("FireCommit");
        private static readonly int CastingCommitParameter = Animator.StringToHash("CastingCommit");
        private static readonly int ReloadCommitParameter = Animator.StringToHash("ReloadCommit");
        private static readonly int HitReactionParameter = Animator.StringToHash("HitReaction");
        private static readonly int DeathTriggerParameter = Animator.StringToHash("DeathTrigger");
        private static readonly int ActorLandedParameter = Animator.StringToHash("ActorLanded");

        private Animator animator;
        private KernelSkeletonBinding skeletonBinding;
        // How far each predicted local action has been confirmed. A held trigger
        // is one action that commits over and over -- the rifle commits every 3
        // ticks for as long as it is held -- and every result for it carries the
        // running total, not the increment, so the last total seen is what turns
        // the next result into "n more shots happened".
        private readonly Dictionary<uint, PredictedAction> predictedActions =
            new Dictionary<uint, PredictedAction>();
        private readonly Queue<uint> predictedActionOrder = new Queue<uint>();
        private Quaternion lastMovementRotation;
        private bool hasMovementRotation;
        // -2 unresolved, -1 resolved-absent. Resolved once against the Animator
        // rather than every frame, and never re-resolved: GetLayerIndex walks the
        // controller's layer names on every call.
        private int upperBodyLayerIndex = UnresolvedLayer;
        private float upperBodyLayerWeight;

        private const int UnresolvedLayer = -2;

        // An accepted action is never told it has ended -- the ABI reports
        // Accepted for a commit and for a completion alike -- so the ledger is
        // bounded instead of cleared. Well past the handful of actions that can
        // overlap in flight.
        private const int MaxTrackedPredictedActions = 32;

        [Header("Animation")]
        [SerializeField]
        [Min(0.001f)]
        [Tooltip(
            "Horizontal kernel speed is divided by this value before writing Animator.Speed. " +
            "Set it to the prefab's authored maximum locomotion speed for a normalized 0..1 value.")]
        private float speedNormalization = 1f;

        /// <summary>
        /// How fast the body may swing to a new facing, in degrees per second.
        /// Nothing on the wire constrains this: the kernel replicates a rotation
        /// that never changes for these actors, so the turn is entirely a
        /// presentation decision made here.
        /// </summary>
        /// <remarks>
        /// This governs every turn, not just the aim-driven ones, and it has to.
        /// An action ends on some frame and facing hands back from the aim to the
        /// velocity on that frame; if one of those two sources snapped, firing once
        /// while running would swing the body onto the reticle and then flick it
        /// back to the run direction. Rate limiting both sides makes the handover
        /// invisible. It costs nothing in the ordinary case, because velocity
        /// direction is already smoothed by the controller's own acceleration.
        /// </remarks>
        [SerializeField]
        [Min(0f)]
        private float maxFacingDegreesPerSecond = 540f;

        [SerializeField]
        [Tooltip(
            "Animator layer that plays the firing action on top of locomotion, or " +
            "empty to leave layer weights alone. Its weight is driven from the " +
            "replicated action state, so the layer must be authored with a default " +
            "weight of 0: an override layer parked at weight 1 writes its masked " +
            "bones every frame, and its empty state writes the bind pose -- the " +
            "upper body sits in its rest pose whenever the actor is not firing.")]
        private string upperBodyLayerName = "Upper Body";

        [SerializeField]
        [Min(0f)]
        [Tooltip(
            "Layer weight units per second when blending the firing layer in and " +
            "out. 0 snaps it.")]
        private float upperBodyBlendSpeed = 12f;

        [Header("Action Triggers")]
        [SerializeField]
        [Tooltip(
            "Animator triggers for this client's own actions, by input binding. " +
            "Rows left empty fall back to FireCommit, or ReloadCommit for a reload.")]
        private LocalActionTriggerBinding[] localActionTriggers;

        [SerializeField]
        [Tooltip(
            "Animator triggers for actions replicated from the server. Leave " +
            "Actor Type on Unknown and Action Template Id on 0 to match every " +
            "actor and every action. Rows left empty fall back to the trigger " +
            "named after the event: FireCommit, CastingCommit, ReloadCommit, " +
            "HitReaction, DeathTrigger.")]
        private RemoteActionTriggerBinding[] remoteActionTriggers;

        public KernelActorType ActorType { get; private set; }
        public uint VisualFlags { get; private set; }
        public KernelActionPhase ActionPhase { get; private set; }
        public uint ActionInstanceId { get; private set; }
        public float Speed { get; private set; }

        /// <summary>
        /// Which way the actor is travelling relative to the way it is facing:
        /// x to its right, y along its forward, as a unit direction on the ground
        /// plane, or zero when it is not moving.
        /// </summary>
        /// <remarks>
        /// Direction only -- <see cref="Speed"/> already carries how fast. Keeping
        /// magnitude out of it means the locomotion thresholds do not move when
        /// speedNormalization is left unconfigured, which it is on most actors.
        ///
        /// This is only ever interesting because facing and movement were pulled
        /// apart: while facing follows velocity the value is pinned at (0, 1) by
        /// construction. It goes negative exactly when the body is held on the aim
        /// and the feet are going the other way.
        /// </remarks>
        public Vector2 LocalMove { get; private set; }
        public uint ActionTemplateId { get; private set; }
        public Vector3 AimDirection { get; private set; }

        /// <summary>
        /// The replicated aim in world space. <see cref="AimDirection"/> is the
        /// same vector in this actor's local frame, which is what an Animator
        /// blend tree wants and what a shot fired along it does not.
        /// </summary>
        public Vector3 WorldAimDirection { get; private set; }
        public bool IsMoving { get; private set; }
        public bool IsGrounded { get; private set; }
        public bool IsFalling { get; private set; }
        public bool IsReloading { get; private set; }
        public bool IsDead { get; private set; }
        public bool IsAiming { get; private set; }
        public bool IsWindup { get; private set; }
        public bool IsFiring { get; private set; }
        public bool IsRecovery { get; private set; }
        public bool IsIdle { get; private set; }
        public bool IsStale { get; private set; }
        public int PredictedCommitCount { get; private set; }

        /// <summary>
        /// Commits the server has confirmed for this actor's own predicted
        /// actions, counting every repeat of a held trigger rather than one per
        /// press.
        /// </summary>
        public int LocalCommitCount { get; private set; }
        public int RemoteCommitCount { get; private set; }
        public int LandedCount { get; private set; }

        public void SetStale(bool stale)
        {
            IsStale = stale;
        }

        public void ApplyContinuousState(RenderEntityState state)
        {
            if (state.status == RenderEntityStatus.Stale)
            {
                IsStale = true;
                return;
            }

            IsStale = false;
            ActorType = state.actor_type;
            VisualFlags = state.visual_flags;
            ActionPhase = state.action.phase;
            ActionInstanceId = state.action.action_instance_id;
            ActionTemplateId = state.action.action_template_id;

            IsDead = HasFlag(KernelConstants.VisualFlagDead);
            IsReloading = !IsDead && HasFlag(KernelConstants.VisualFlagReloading);
            IsMoving = !IsDead && HasFlag(KernelConstants.VisualFlagMoving);
            IsAiming = !IsDead && HasFlag(KernelConstants.VisualFlagAiming);
            IsGrounded = HasFlag(KernelConstants.VisualFlagGrounded);
            IsFalling = HasFlag(KernelConstants.VisualFlagFalling);
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

            float normalization = Mathf.Max(0.001f, speedNormalization);
            Speed = new Vector2(state.velocity.x, state.velocity.z).magnitude /
                normalization;

            Vector3 worldAim = new Vector3(
                state.aim_direction.x,
                state.aim_direction.y,
                state.aim_direction.z);
            WorldAimDirection = worldAim.sqrMagnitude > 0.000001f
                ? worldAim.normalized
                : Vector3.zero;

            // Facing has to settle before the aim is taken into local space below,
            // or the animator is fed this frame's aim measured against last frame's
            // rotation -- which reads as the upper body lagging the turn.
            ApplyMovementFacing(state);

            AimDirection = WorldAimDirection != Vector3.zero
                ? transform.InverseTransformDirection(WorldAimDirection)
                : Vector3.zero;
            LocalMove = ResolveLocalMove(
                new Vector3(state.velocity.x, 0f, state.velocity.z),
                IsAimDrivenFacing());

            Animator target = GetAnimator();
            SetFloatIfPresent(target, SpeedParameter, Speed);
            SetFloatIfPresent(target, MoveXParameter, LocalMove.x);
            SetFloatIfPresent(target, MoveYParameter, LocalMove.y);
            SetFloatIfPresent(target, AimXParameter, AimDirection.x);
            SetFloatIfPresent(target, AimYParameter, AimDirection.y);
            SetFloatIfPresent(target, AimZParameter, AimDirection.z);
            SetBoolIfPresent(target, MovingParameter, IsMoving);
            SetBoolIfPresent(target, GroundedParameter, IsGrounded);
            SetBoolIfPresent(target, FallingParameter, IsFalling);
            SetBoolIfPresent(target, ReloadingParameter, IsReloading);
            SetBoolIfPresent(target, DeadParameter, IsDead);
            SetBoolIfPresent(target, AimingParameter, IsAiming);
            SetBoolIfPresent(target, WindupParameter, IsWindup);
            SetBoolIfPresent(target, FiringParameter, IsFiring);
            SetBoolIfPresent(target, RecoveryParameter, IsRecovery);
            SetBoolIfPresent(target, IdleParameter, IsIdle);
            SetIntegerIfPresent(target, ActionPhaseParameter, (int)ActionPhase);
            ApplyUpperBodyLayerWeight(target);
        }

        /// <summary>
        /// Blends the firing layer in while an action is running and out once it
        /// ends. The layer is masked to the upper body, so locomotion on the base
        /// layer keeps driving the legs throughout -- which is what the kernel
        /// replicates: an agent can be moving and firing on the same tick.
        /// </summary>
        private void ApplyUpperBodyLayerWeight(Animator target)
        {
            int layer = ResolveUpperBodyLayer(target);
            if (layer < 0)
            {
                return;
            }

            float goal = ResolveUpperBodyLayerGoal();
            upperBodyLayerWeight = upperBodyBlendSpeed > 0f
                ? Mathf.MoveTowards(
                    upperBodyLayerWeight,
                    goal,
                    upperBodyBlendSpeed * Time.unscaledDeltaTime)
                : goal;
            target.SetLayerWeight(layer, upperBodyLayerWeight);
        }

        /// <summary>
        /// Whether the upper body layer should be carrying the pose this frame.
        /// </summary>
        /// <remarks>
        /// ActionPhase covers windup and recovery as well; VisualFlagFiring is the
        /// same signal from the other side. Either one alone would leave a gap in
        /// weapons that spend most of an action outside Active.
        ///
        /// Aiming belongs here for a different reason: holding a weapon up is a
        /// pose the actor keeps for as long as the button is down, with no action
        /// running at all. Without it the aim layer would only ever surface during
        /// the shot itself, and the raise and lower animations would never play.
        /// </remarks>
        public float ResolveUpperBodyLayerGoal()
        {
            return IsAiming || IsFiring || ActionPhase != KernelActionPhase.None
                ? 1f
                : 0f;
        }

        private int ResolveUpperBodyLayer(Animator target)
        {
            if (target == null || target.runtimeAnimatorController == null)
            {
                return -1;
            }

            if (upperBodyLayerIndex == UnresolvedLayer)
            {
                upperBodyLayerIndex = string.IsNullOrEmpty(upperBodyLayerName)
                    ? -1
                    : target.GetLayerIndex(upperBodyLayerName);
            }

            return upperBodyLayerIndex;
        }

        private void ApplyMovementFacing(RenderEntityState state)
        {
            if (HasKernelPosedRig())
            {
                // NetworkRenderStateApplier writes the replicated rotation just
                // before this runs, and everything below would overwrite it. For a
                // rig the kernel poses, that rotation is not decoration: the leg
                // solve places every foot in world space using it, and the pose
                // arrives as bone-local transforms under this root. Turning the
                // root away from it carries the whole leg rig off the footholds it
                // was solved for -- feet swing and slide while the body keeps
                // walking straight.
                //
                // Deriving facing from velocity is also strictly worse here. The
                // kernel already slews the heading at the actor's authored
                // max_yaw_degrees_per_second; velocity direction has no such limit
                // and snaps the instant the controller slides on terrain.
                return;
            }

            ApplyFacing(
                WorldAimDirection,
                new Vector3(state.velocity.x, 0f, state.velocity.z),
                IsAimDrivenFacing(),
                Time.unscaledDeltaTime);
        }

        /// <summary>
        /// True while the body should point where the weapon points rather than
        /// where the feet are going.
        /// </summary>
        /// <remarks>
        /// Aiming is the obvious case, and it is what lets a player walk backwards
        /// without the character turning round to do it -- the move vector is
        /// already camera-relative, so leaving facing on the aim is the whole of
        /// backpedalling.
        ///
        /// Firing is the less obvious one, and it is the reason a shot used to
        /// leave in a direction the character was visibly not pointing: the aim
        /// comes from the reticle while facing came from velocity, so running left
        /// and shooting forward pointed the body west and the bullet north. Windup
        /// and recovery are included so the turn starts with the animation rather
        /// than on the frame the projectile spawns.
        /// </remarks>
        private bool IsAimDrivenFacing()
        {
            return !IsDead && (IsAiming || IsWindup || IsFiring || IsRecovery);
        }

        /// <summary>
        /// Points the body for one frame. Public, and taking its inputs rather
        /// than reading them, so the turn can be stepped with a real delta from a
        /// test -- <see cref="Time.unscaledDeltaTime"/> is zero in edit mode, which
        /// would freeze every rate-limited turn at its starting angle.
        /// </summary>
        public void ApplyFacing(
            Vector3 worldAimDirection,
            Vector3 velocity,
            bool aimDriven,
            float deltaTime)
        {
            if (TryResolveFacingForward(
                    worldAimDirection,
                    velocity,
                    aimDriven,
                    out Vector3 forward))
            {
                Quaternion desired = Quaternion.LookRotation(forward, Vector3.up);
                // The first facing an actor ever resolves has nothing to turn away
                // from, so it is adopted outright rather than crawled to from
                // whatever rotation the spawn happened to use.
                lastMovementRotation =
                    hasMovementRotation && maxFacingDegreesPerSecond > 0f
                        ? Quaternion.RotateTowards(
                            lastMovementRotation,
                            desired,
                            maxFacingDegreesPerSecond * Mathf.Max(0f, deltaTime))
                        : desired;
                hasMovementRotation = true;
            }

            if (hasMovementRotation)
            {
                transform.rotation = lastMovementRotation;
            }
        }

        /// <summary>
        /// Takes a world velocity into the actor's own frame. Must run after facing
        /// has settled for the frame, or the feet are measured against a rotation
        /// the body has already left.
        /// </summary>
        /// <remarks>
        /// While facing is not aim-driven the actor is running forwards by
        /// definition -- the body is chasing the velocity, and reports (0, 1)
        /// outright rather than being measured. Measuring it would report the turn
        /// instead of the travel: the body swings at a limited rate, so for the
        /// fraction of a second after aim is released while backpedalling, the
        /// velocity is genuinely sideways relative to a body that has not finished
        /// coming about, and the legs would flick through a sidestep on the way.
        /// </remarks>
        private Vector2 ResolveLocalMove(Vector3 velocity, bool aimDriven)
        {
            if (velocity.sqrMagnitude <= MovementFacingSpeedThresholdSqr)
            {
                return Vector2.zero;
            }

            if (!aimDriven)
            {
                return new Vector2(0f, 1f);
            }

            Vector3 local = transform.InverseTransformDirection(velocity.normalized);
            return new Vector2(local.x, local.z);
        }

        /// <summary>
        /// Picks the direction the body should point this frame, flattened onto the
        /// ground plane because a body yaws and does not pitch. Returns false when
        /// neither source has anything to say -- an actor standing still and not
        /// aiming keeps whatever facing it already had.
        /// </summary>
        public static bool TryResolveFacingForward(
            Vector3 worldAimDirection,
            Vector3 velocity,
            bool aimDriven,
            out Vector3 forward)
        {
            if (aimDriven)
            {
                Vector3 flatAim = new Vector3(worldAimDirection.x, 0f, worldAimDirection.z);
                if (flatAim.sqrMagnitude > MovementFacingSpeedThresholdSqr)
                {
                    forward = flatAim.normalized;
                    return true;
                }
            }

            Vector3 flatVelocity = new Vector3(velocity.x, 0f, velocity.z);
            if (flatVelocity.sqrMagnitude > MovementFacingSpeedThresholdSqr)
            {
                forward = flatVelocity.normalized;
                return true;
            }

            forward = Vector3.zero;
            return false;
        }

        public void BeginPredictedAction(KernelActionIntent intent)
        {
            if (IsStale ||
                intent.action_instance_id == 0 ||
                predictedActions.ContainsKey(intent.action_instance_id))
            {
                return;
            }

            RememberPredictedAction(
                intent.action_instance_id,
                new PredictedAction(intent.binding_id, 0));
            PredictedCommitCount++;
            int trigger = TriggerFor(intent.binding_id);
            SetTriggerIfPresent(GetAnimator(), trigger);
        }

        /// <summary>
        /// Folds one authoritative result into the local player's predicted
        /// action, and reports how many commits it confirmed that had not been
        /// seen before.
        /// </summary>
        /// <remarks>
        /// The press is not the shot. A hold-fire weapon presses once and then
        /// commits on its own cadence for as long as the trigger is down, and
        /// every one of those commits is a shot that has to be drawn. The result
        /// carries the running total, so the difference against the total last
        /// seen is the number of shots this result just announced.
        /// </remarks>
        public int ApplyLocalActionResult(KernelLocalActionResult result)
        {
            if (result.action_instance_id == 0 ||
                !predictedActions.TryGetValue(
                    result.action_instance_id,
                    out PredictedAction predicted))
            {
                return 0;
            }

            // Accepted confirms the prediction without replaying its one-shot.
            // Corrected/rejected end the action and clear only presentation state;
            // gameplay rollback remains owned by the kernel and authoritative
            // snapshots.
            bool ended = result.result == KernelLocalActionResultType.Corrected ||
                result.result == KernelLocalActionResultType.Rejected;
            int newCommits = 0;
            if (!ended && result.confirmed_commit_count > predicted.confirmedCommitCount)
            {
                newCommits =
                    result.confirmed_commit_count - predicted.confirmedCommitCount;
                predictedActions[result.action_instance_id] = new PredictedAction(
                    predicted.binding,
                    result.confirmed_commit_count);
            }

            if (ended)
            {
                predictedActions.Remove(result.action_instance_id);
            }

            if (IsStale)
            {
                return 0;
            }

            if (ended)
            {
                SetBoolIfPresent(GetAnimator(), FiringParameter, false);
                SetBoolIfPresent(GetAnimator(), ReloadingParameter, false);
                return 0;
            }

            if (newCommits > 0)
            {
                LocalCommitCount += newCommits;
                SetTriggerIfPresent(GetAnimator(), TriggerFor(predicted.binding));
            }

            return newCommits;
        }

        private void RememberPredictedAction(uint actionInstanceId, PredictedAction predicted)
        {
            predictedActions[actionInstanceId] = predicted;
            predictedActionOrder.Enqueue(actionInstanceId);
            while (predictedActionOrder.Count > MaxTrackedPredictedActions)
            {
                predictedActions.Remove(predictedActionOrder.Dequeue());
            }
        }

        public void PlayRemoteCommit(
            KernelRemoteActionPresentationEvent remoteEvent,
            uint commitIndex)
        {
            if (IsStale)
            {
                return;
            }

            RemoteCommitCount++;
            SetTriggerIfPresent(GetAnimator(), ResolveRemoteActionTrigger(remoteEvent));
        }

        public void PlayActorLanded()
        {
            if (IsStale)
            {
                return;
            }

            LandedCount++;
            SetTriggerIfPresent(GetAnimator(), ActorLandedParameter);
        }

        private bool HasFlag(uint flag)
        {
            return (VisualFlags & flag) != 0;
        }

        // Whether this actor's skeleton is posed by the kernel rather than by an
        // Animator. Resolved lazily and never cached negatively, for the same
        // reason GetAnimator does it this way: the rig is a child of the catalog
        // prefab and a view can be added before that child exists.
        private bool HasKernelPosedRig()
        {
            if (skeletonBinding == null)
            {
                skeletonBinding = GetComponentInChildren<KernelSkeletonBinding>(true);
            }
            return skeletonBinding != null;
        }

        private Animator GetAnimator()
        {
            if (animator == null)
            {
                // USER ASSET HOOK:
                // Keep NetworkActorView on the kernel-driven ActorRoot, then put the
                // model and its Animator on a child named Visual. Replace the prefab
                // reference in NetworkPrefabCatalog; no gameplay/native change is needed.
                animator = GetComponentInChildren<Animator>();
                if (animator != null)
                {
                    // ActorRoot movement is authoritative kernel presentation. Animation
                    // root motion must never move it between render-state applications.
                    animator.applyRootMotion = false;
                }
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

        /// <summary>
        /// Replaces the authored remote-action triggers. The inspector is the
        /// usual way in; this is for a caller that builds the table itself.
        /// </summary>
        public void ConfigureRemoteActionTriggers(RemoteActionTriggerBinding[] bindings)
        {
            remoteActionTriggers = bindings;
        }

        /// <summary>
        /// The Animator trigger <paramref name="remoteEvent"/> sets on this
        /// actor: the most specific authored row that describes it, or the
        /// trigger named after the event when no row does.
        /// </summary>
        public int ResolveRemoteActionTrigger(
            KernelRemoteActionPresentationEvent remoteEvent)
        {
            RemoteActionTriggerBinding best = null;
            int bestSpecificity = -1;

            if (remoteActionTriggers != null)
            {
                for (int index = 0; index < remoteActionTriggers.Length; ++index)
                {
                    RemoteActionTriggerBinding candidate = remoteActionTriggers[index];
                    int specificity = SpecificityOf(candidate, remoteEvent);
                    // Strictly greater, so equally specific rows resolve to the
                    // first one authored rather than the last.
                    if (specificity > bestSpecificity)
                    {
                        bestSpecificity = specificity;
                        best = candidate;
                    }
                }
            }

            return best != null
                ? Animator.StringToHash(best.animatorTrigger)
                : DefaultTriggerFor(remoteEvent.event_type);
        }

        /// <summary>
        /// How well one row describes <paramref name="remoteEvent"/>, or -1 for a
        /// row that does not describe it at all.
        /// </summary>
        /// <remarks>
        /// The event type is the row's subject and always has to match. The other
        /// two selectors each either name a value or wave everything through with
        /// zero, and naming one is worth more than waving -- naming the actor
        /// more than naming the action, since an actor's rig is what owns the
        /// trigger being set.
        /// </remarks>
        private int SpecificityOf(
            RemoteActionTriggerBinding candidate,
            KernelRemoteActionPresentationEvent remoteEvent)
        {
            if (candidate == null ||
                candidate.eventType != remoteEvent.event_type ||
                string.IsNullOrEmpty(candidate.animatorTrigger))
            {
                return -1;
            }

            int specificity = 0;
            if (candidate.actorType != KernelActorType.Unknown)
            {
                if (candidate.actorType != ActorType)
                {
                    return -1;
                }

                specificity += 2;
            }

            if (candidate.actionTemplateId != 0)
            {
                if (candidate.actionTemplateId != remoteEvent.action_template_id)
                {
                    return -1;
                }

                specificity += 1;
            }

            return specificity;
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

        private static void SetFloatIfPresent(Animator target, int parameter, float value)
        {
            if (HasParameter(target, parameter, AnimatorControllerParameterType.Float))
            {
                target.SetFloat(parameter, value);
            }
        }

        private static void SetIntegerIfPresent(Animator target, int parameter, int value)
        {
            if (HasParameter(target, parameter, AnimatorControllerParameterType.Int))
            {
                target.SetInteger(parameter, value);
            }
        }

        private static void SetTriggerIfPresent(Animator target, int parameter)
        {
            if (HasParameter(target, parameter, AnimatorControllerParameterType.Trigger))
            {
                target.SetTrigger(parameter);
            }
        }

        private readonly struct PredictedAction
        {
            public readonly KernelActionBinding binding;
            public readonly ushort confirmedCommitCount;

            public PredictedAction(
                KernelActionBinding binding,
                ushort confirmedCommitCount)
            {
                this.binding = binding;
                this.confirmedCommitCount = confirmedCommitCount;
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
