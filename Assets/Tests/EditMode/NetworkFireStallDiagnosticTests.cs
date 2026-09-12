using NetworkExample.Kernel;
using NetworkExample.UnityDemo.Input;
using NUnit.Framework;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class NetworkFireStallDiagnosticTests
    {
        private const float StallSeconds = 1.5f;
        private const float Step = 0.5f;

        private NetworkFireStallDiagnostic diagnostic;

        [SetUp]
        public void SetUp()
        {
            diagnostic = new NetworkFireStallDiagnostic(StallSeconds);
        }

        private static KernelActionRuntimeView Action(
            uint actionInstanceId,
            uint commitCount,
            KernelActionPhase phase = KernelActionPhase.Active)
        {
            return new KernelActionRuntimeView
            {
                action_template_id = 7,
                action_instance_id = actionInstanceId,
                phase = phase,
                commit_count = commitCount,
            };
        }

        /// <summary>
        /// Runs the watchdog past its threshold and hands back the one report it
        /// produced, or null.
        /// </summary>
        private string RunHeld(
            uint sampledHeldActionId,
            KernelActionRuntimeView action,
            uint visualFlags = 0U,
            bool fireHeld = true,
            int steps = 8)
        {
            string captured = null;
            for (int index = 0; index < steps; ++index)
            {
                if (diagnostic.Observe(
                        Step,
                        fireHeld,
                        sampledHeldActionId,
                        true,
                        action,
                        visualFlags,
                        out string report) && captured == null)
                {
                    captured = report;
                }
            }

            return captured;
        }

        [Test]
        public void Observe_WithTriggerUp_NeverReports()
        {
            Assert.That(RunHeld(0, Action(0, 0), fireHeld: false), Is.Null);
        }

        /// <summary>
        /// Rapid tapping is the shape that actually reproduces the stall. The
        /// watchdog must stay armed through the gaps between taps rather than
        /// disarming every time the button comes up for a frame.
        /// </summary>
        [Test]
        public void Observe_WhileTapping_StillReportsAStall()
        {
            string captured = null;
            for (int index = 0; index < 16; ++index)
            {
                // Down, up, down, up ... at 0.25s a step: every gap is inside the
                // attempt window, so the player never stops asking.
                bool down = (index % 2) == 0;
                if (diagnostic.Observe(
                        0.25f, down, 0, true, Action(0, 0, KernelActionPhase.None),
                        0U, out string report) && captured == null)
                {
                    captured = report;
                }
            }

            Assert.That(captured, Is.Not.Null,
                "Tapping through a stall has to report, not reset the watchdog.");
        }

        /// <summary>
        /// Taps far enough apart are a player who stopped asking, not a stall.
        /// </summary>
        [Test]
        public void Observe_WithTapsBeyondTheAttemptWindow_DoesNotReport()
        {
            string captured = null;
            for (int index = 0; index < 16; ++index)
            {
                bool down = (index % 8) == 0;
                if (diagnostic.Observe(
                        0.5f, down, 0, true, Action(0, 0, KernelActionPhase.None),
                        0U, out string report) && captured == null)
                {
                    captured = report;
                }
            }

            Assert.That(captured, Is.Null);
        }

        [Test]
        public void Observe_WhileCommitsAdvance_NeverReports()
        {
            string captured = null;
            for (uint commit = 0; commit < 12; ++commit)
            {
                if (diagnostic.Observe(
                        Step, true, 42, true, Action(42, commit), 0U, out string report) &&
                    captured == null)
                {
                    captured = report;
                }
            }

            Assert.That(captured, Is.Null);
        }

        [Test]
        public void Observe_WhenDead_NeverReports()
        {
            Assert.That(
                RunHeld(42, Action(42, 3), KernelConstants.VisualFlagDead),
                Is.Null);
        }

        [Test]
        public void Observe_BeforeTheThresholdElapses_DoesNotReport()
        {
            Assert.That(
                diagnostic.Observe(Step, true, 42, true, Action(42, 3), 0U, out _),
                Is.False);
            Assert.That(
                diagnostic.Observe(Step, true, 42, true, Action(42, 3), 0U, out _),
                Is.False);
        }

        /// <summary>
        /// The sampler is feeding an action the authoritative state does not name
        /// and no result ever ended it -- the kernel dropped it silently, which is
        /// the one shape the client cannot detect from its own bookkeeping.
        /// </summary>
        [Test]
        public void Observe_WhenTheKernelNoLongerNamesTheSampledAction_SaysItWasDropped()
        {
            string report = RunHeld(42, Action(0, 0, KernelActionPhase.None));

            Assert.That(report, Is.Not.Null);
            Assert.That(report, Does.Contain("sampler_held_action=42"));
            Assert.That(report, Does.Contain("authoritative_action=0"));
            Assert.That(report, Does.Contain("dropped it silently"));
        }

        [Test]
        public void Observe_WhenTheSamplerHoldsNothing_PointsAtTheRefusalReason()
        {
            string report = RunHeld(0, Action(0, 0, KernelActionPhase.None));

            Assert.That(report, Is.Not.Null);
            Assert.That(report, Does.Contain("refused"));
        }

        [Test]
        public void Observe_WhenTheActionIsLiveButNotCommitting_BlamesTheKernel()
        {
            string report = RunHeld(42, Action(42, 3, KernelActionPhase.Recovery));

            Assert.That(report, Is.Not.Null);
            Assert.That(report, Does.Contain("stopped"));
            Assert.That(report, Does.Contain("inside the kernel"));
            Assert.That(report, Does.Contain("phase=Recovery"));
        }

        [Test]
        public void Observe_DuringOneStall_ReportsExactlyOnce()
        {
            int reports = 0;
            for (int index = 0; index < 20; ++index)
            {
                if (diagnostic.Observe(
                        Step, true, 42, true, Action(42, 3), 0U, out _))
                {
                    ++reports;
                }
            }

            Assert.That(reports, Is.EqualTo(1));
        }

        /// <summary>
        /// A stall that recovers and happens again is two separate events.
        /// </summary>
        [Test]
        public void Observe_AfterRecovering_ReportsTheNextStallAgain()
        {
            Assert.That(RunHeld(42, Action(42, 3)), Is.Not.Null);

            // One confirmed commit is progress, and re-arms the watchdog.
            diagnostic.Observe(Step, true, 42, true, Action(42, 4), 0U, out _);

            string second = RunHeld(42, Action(42, 4));

            Assert.That(second, Is.Not.Null);
        }

        /// <summary>
        /// Ammo is not in the render state, so a weapon left empty by a reload
        /// that damage cancelled is only visible as the reason it keeps refusing.
        /// </summary>
        [Test]
        public void Observe_AfterRefusals_NamesTheReasonAndHowMany()
        {
            for (int index = 0; index < 5; ++index)
            {
                diagnostic.NoteActionResult(
                    KernelLocalActionResultType.Rejected,
                    KernelLocalActionResultReason.NoAmmo);
            }

            string report = RunHeld(0, Action(0, 0, KernelActionPhase.None));

            Assert.That(report, Is.Not.Null);
            Assert.That(report, Does.Contain("last_refusal=Rejected(NoAmmo) x5"));
        }

        [Test]
        public void Observe_WithNoRefusals_SaysSo()
        {
            string report = RunHeld(42, Action(42, 3));

            Assert.That(report, Is.Not.Null);
            Assert.That(report, Does.Contain("last_refusal=none"));
        }

        /// <summary>
        /// A stuck reloading flag is the other shape a cancelled reload leaves
        /// behind, and that one the render state does carry.
        /// </summary>
        [Test]
        public void Observe_WithReloadingFlagStuck_ShowsItInTheReport()
        {
            string report = RunHeld(
                0,
                Action(0, 0, KernelActionPhase.None),
                KernelConstants.VisualFlagReloading);

            Assert.That(report, Is.Not.Null);
            Assert.That(report, Does.Contain("flags=Reloading"));
        }

        [Test]
        public void NoteActionResult_WhenAccepted_DoesNotCountAsARefusal()
        {
            diagnostic.NoteActionResult(
                KernelLocalActionResultType.Accepted,
                KernelLocalActionResultReason.None);

            string report = RunHeld(42, Action(42, 3));

            Assert.That(report, Does.Contain("last_refusal=none"));
        }

        /// <summary>
        /// Refusals belong to the stall they happened in; progress clears them so
        /// the next report cannot be read against stale evidence.
        /// </summary>
        [Test]
        public void Observe_AfterProgress_ForgetsEarlierRefusals()
        {
            diagnostic.NoteActionResult(
                KernelLocalActionResultType.Rejected,
                KernelLocalActionResultReason.NoAmmo);
            diagnostic.Observe(Step, true, 42, true, Action(42, 1), 0U, out _);
            diagnostic.Observe(Step, true, 42, true, Action(42, 2), 0U, out _);

            string report = RunHeld(42, Action(42, 2));

            Assert.That(report, Does.Contain("last_refusal=none"));
        }

        /// <summary>
        /// A local actor missing from the render set explains the silence by
        /// itself, and staying quiet about it is how a stall goes uninvestigated.
        /// </summary>
        [Test]
        public void Observe_WithNoAuthoritativeState_SaysTheActorIsMissing()
        {
            string captured = null;
            for (int index = 0; index < 10; ++index)
            {
                if (diagnostic.Observe(
                        Step, true, 42, false, default, 0U, out string report) &&
                    captured == null)
                {
                    captured = report;
                }
            }

            Assert.That(captured, Is.Not.Null);
            Assert.That(captured, Does.Contain("no authoritative state"));
        }
    }
}
