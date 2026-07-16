using System;

namespace NetworkExample.UnityDemo.Common
{
    /// <summary>
    /// Paces client input submissions at the server simulation rate. This is a
    /// client-side compatibility workaround until prediction is tick-driven in
    /// the kernel.
    /// </summary>
    public sealed class NetworkInputSubmissionClock
    {
        private readonly double intervalSeconds;
        private double accumulatedSeconds;

        public NetworkInputSubmissionClock(float submissionRateHz)
        {
            if (submissionRateHz <= 0f ||
                float.IsNaN(submissionRateHz) ||
                float.IsInfinity(submissionRateHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(submissionRateHz),
                    "Input submission rate must be finite and greater than zero.");
            }

            intervalSeconds = 1.0 / submissionRateHz;
        }

        public void Reset()
        {
            accumulatedSeconds = 0.0;
        }

        public bool ShouldSubmit(float deltaSeconds)
        {
            if (deltaSeconds <= 0f ||
                float.IsNaN(deltaSeconds) ||
                float.IsInfinity(deltaSeconds))
            {
                return false;
            }

            accumulatedSeconds += deltaSeconds;
            if (accumulatedSeconds < intervalSeconds)
            {
                return false;
            }

            // Never build a catch-up queue after a long render stall. Submitting
            // that queue on following frames would temporarily recreate the same
            // prediction oversimulation this clock is intended to prevent.
            if (accumulatedSeconds >= intervalSeconds * 2.0)
            {
                accumulatedSeconds = 0.0;
            }
            else
            {
                accumulatedSeconds -= intervalSeconds;
            }

            return true;
        }
    }
}
