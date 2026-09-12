using System.Text;
using NetworkExample.Kernel;

namespace NetworkExample.UnityDemo.Input
{
    /// <summary>
    /// Watches for the one symptom that is hard to reason about from the outside:
    /// the trigger is held down and nothing is coming out of it.
    /// </summary>
    /// <remarks>
    /// "Firing stopped" has several causes that look identical in game, and the
    /// client can only see one of them from its own bookkeeping. Pairing what the
    /// sampler believes against what the authoritative state actually carries
    /// separates them:
    ///
    /// - the sampler holds an action id the kernel's action no longer names, and
    ///   no result ever arrived to say so -- the action was dropped silently and
    ///   the client is talking to something that is gone;
    /// - the sampler holds nothing because every attempt is being refused, which
    ///   the action result log names the reason for;
    /// - the kernel's action is live and still ours, but its commit count has
    ///   stopped advancing -- the stall is behind the ABI and no amount of client
    ///   bookkeeping will fix it.
    ///
    /// Edge triggered: one report per stall, not one per frame.
    /// </remarks>
    public sealed class NetworkFireStallDiagnostic
    {
        /// <summary>
        /// How long after the trigger comes up the player still counts as trying
        /// to fire. Rapid tapping is the same intent as holding, and it is the
        /// case that actually reproduces the stall, so the watchdog must not
        /// disarm itself in the gaps between taps.
        /// </summary>
        private const float AttemptWindowSeconds = 1f;

        private readonly float stallSeconds;
        private float sinceLastPressSeconds;
        private float heldWithoutProgressSeconds;
        private uint lastActionInstanceId;
        private uint lastCommitCount;
        private bool hasProgressBaseline;
        private bool reported;
        private bool missingAuthoritativeState;
        private int outstandingActionCount;
        private uint totalActionsAllocated;
        private KernelLocalActionResultType lastRefusal;
        private KernelLocalActionResultReason lastRefusalReason;
        private int refusalsSinceProgress;

        public NetworkFireStallDiagnostic(float stallSeconds)
        {
            this.stallSeconds = stallSeconds > 0f ? stallSeconds : 0.5f;
        }

        /// <summary>
        /// Records one action result the kernel sent back. Refusals are the other
        /// half of the picture: the render state says what the actor is doing, and
        /// this says what it keeps saying no to and why. Ammo in particular is not
        /// in the render state at all, so a weapon left empty by a reload that
        /// damage cancelled is only visible here.
        /// </summary>
        public void NoteActionResult(
            KernelLocalActionResultType result,
            KernelLocalActionResultReason reason)
        {
            if (result == KernelLocalActionResultType.Accepted)
            {
                return;
            }

            lastRefusal = result;
            lastRefusalReason = reason;
            ++refusalsSinceProgress;
        }

        /// <summary>
        /// Folds one frame in. Returns true exactly once per stall, with the
        /// report to log.
        /// </summary>
        public bool Observe(
            float deltaSeconds,
            bool fireHeld,
            uint sampledHeldActionId,
            bool hasAuthoritativeState,
            KernelActionRuntimeView authoritativeAction,
            uint visualFlags,
            out string report)
        {
            return Observe(
                deltaSeconds,
                fireHeld,
                sampledHeldActionId,
                hasAuthoritativeState,
                authoritativeAction,
                visualFlags,
                0,
                0U,
                out report);
        }

        public bool Observe(
            float deltaSeconds,
            bool fireHeld,
            uint sampledHeldActionId,
            bool hasAuthoritativeState,
            KernelActionRuntimeView authoritativeAction,
            uint visualFlags,
            int outstandingActionCount,
            uint totalActionsAllocated,
            out string report)
        {
            report = null;
            this.outstandingActionCount = outstandingActionCount;
            this.totalActionsAllocated = totalActionsAllocated;

            // Holding and tapping are one intent. Tracking the gap since the last
            // press instead of the button's current state keeps the watchdog armed
            // across a burst of taps, which is the shape that actually stalls.
            if (fireHeld)
            {
                sinceLastPressSeconds = 0f;
            }
            else
            {
                sinceLastPressSeconds += deltaSeconds > 0f ? deltaSeconds : 0f;
            }

            bool tryingToFire = sinceLastPressSeconds < AttemptWindowSeconds;

            // A player who stopped asking, an actor that is dead, and a frame with
            // no authoritative state to compare against are all silences with an
            // ordinary explanation.
            bool dead = (visualFlags & KernelConstants.VisualFlagDead) != 0U;
            if (!tryingToFire || dead)
            {
                float carriedSincePress = sinceLastPressSeconds;
                Reset();
                sinceLastPressSeconds = carriedSincePress;
                return false;
            }

            // A missing local actor is itself worth reporting: it would explain
            // the silence, and staying quiet about it is how a stall goes
            // uninvestigated.
            if (!hasAuthoritativeState)
            {
                authoritativeAction = default;
            }

            missingAuthoritativeState = !hasAuthoritativeState;

            // The first frame of a hold only establishes what to compare against.
            // Treating it as progress would wipe the refusals already collected
            // for this stall, which are often the whole explanation.
            bool hadBaseline = hasProgressBaseline;
            bool progressed =
                !hadBaseline ||
                authoritativeAction.action_instance_id != lastActionInstanceId ||
                authoritativeAction.commit_count != lastCommitCount;
            lastActionInstanceId = authoritativeAction.action_instance_id;
            lastCommitCount = authoritativeAction.commit_count;
            hasProgressBaseline = true;

            if (progressed)
            {
                heldWithoutProgressSeconds = 0f;
                reported = false;
                if (hadBaseline)
                {
                    refusalsSinceProgress = 0;
                }

                return false;
            }

            heldWithoutProgressSeconds += deltaSeconds > 0f ? deltaSeconds : 0f;
            if (heldWithoutProgressSeconds < stallSeconds || reported)
            {
                return false;
            }

            reported = true;
            report = BuildReport(sampledHeldActionId, authoritativeAction, visualFlags);
            return true;
        }

        public void Reset()
        {
            sinceLastPressSeconds = AttemptWindowSeconds;
            heldWithoutProgressSeconds = 0f;
            lastActionInstanceId = 0;
            lastCommitCount = 0;
            hasProgressBaseline = false;
            reported = false;
            missingAuthoritativeState = false;
            refusalsSinceProgress = 0;
            lastRefusal = KernelLocalActionResultType.Accepted;
            lastRefusalReason = KernelLocalActionResultReason.None;
        }

        private string BuildReport(
            uint sampledHeldActionId,
            KernelActionRuntimeView authoritativeAction,
            uint visualFlags)
        {
            StringBuilder builder = new StringBuilder(256);
            builder.Append("Fire input active for ")
                .Append(heldWithoutProgressSeconds.ToString("F2"))
                .Append("s with no commit progress. sampler_held_action=")
                .Append(sampledHeldActionId)
                .Append(" authoritative_action=")
                .Append(authoritativeAction.action_instance_id)
                .Append(" template=")
                .Append(authoritativeAction.action_template_id)
                .Append(" phase=")
                .Append(authoritativeAction.phase)
                .Append(" commits=")
                .Append(authoritativeAction.commit_count)
                .Append(" flags=");
            AppendVisualFlags(builder, visualFlags);
            builder.Append(" outstanding=")
                .Append(outstandingActionCount)
                .Append(" allocated=")
                .Append(totalActionsAllocated);
            if (refusalsSinceProgress > 0)
            {
                builder.Append(" last_refusal=")
                    .Append(lastRefusal)
                    .Append('(')
                    .Append(lastRefusalReason)
                    .Append(") x")
                    .Append(refusalsSinceProgress);
            }
            else
            {
                builder.Append(" last_refusal=none");
            }

            builder.Append(" -- ").Append(
                missingAuthoritativeState
                    ? "the local actor has no authoritative state in this frame's " +
                        "render set at all, so nothing can act on input: look at " +
                        "replication before the action system."
                    : Interpret(sampledHeldActionId, authoritativeAction));
            return builder.ToString();
        }

        /// <summary>
        /// Names which of the three shapes this stall is, so the log says what to
        /// go and look at rather than leaving the numbers to be decoded by hand.
        /// </summary>
        private static string Interpret(
            uint sampledHeldActionId,
            KernelActionRuntimeView authoritativeAction)
        {
            if (sampledHeldActionId == 0)
            {
                return "the sampler is holding no action: every attempt is being " +
                    "refused, or none is being made. The refusal above names why.";
            }

            if (authoritativeAction.action_instance_id != sampledHeldActionId)
            {
                return "the sampler is feeding an action the authoritative state " +
                    "does not name, and no result ended it: the kernel dropped it " +
                    "silently and the client cannot tell without this check.";
            }

            return "the action is live and still ours, but it has stopped " +
                "committing: the stall is inside the kernel, not in the client's " +
                "bookkeeping.";
        }

        private static void AppendVisualFlags(StringBuilder builder, uint visualFlags)
        {
            int written = 0;
            written += AppendFlag(builder, visualFlags, KernelConstants.VisualFlagReloading, "Reloading", written);
            written += AppendFlag(builder, visualFlags, KernelConstants.VisualFlagAiming, "Aiming", written);
            written += AppendFlag(builder, visualFlags, KernelConstants.VisualFlagFiring, "Firing", written);
            written += AppendFlag(builder, visualFlags, KernelConstants.VisualFlagMoving, "Moving", written);
            if (written == 0)
            {
                builder.Append("none");
            }
        }

        private static int AppendFlag(
            StringBuilder builder,
            uint visualFlags,
            uint flag,
            string name,
            int written)
        {
            if ((visualFlags & flag) == 0U)
            {
                return 0;
            }

            if (written > 0)
            {
                builder.Append('|');
            }

            builder.Append(name);
            return 1;
        }
    }
}
