namespace NetworkExample.UnityDemo.Common
{
    public sealed class NetworkPresentationClock
    {
        private double accumulatedSeconds;

        public void Reset()
        {
            accumulatedSeconds = 0.0;
        }

        public ulong Advance(float deltaSeconds)
        {
            if (deltaSeconds > 0f)
            {
                accumulatedSeconds += deltaSeconds;
            }

            return (ulong)(accumulatedSeconds * 1000000.0);
        }
    }
}
