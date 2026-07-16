using System;
using NetworkExample.UnityDemo.Common;
using NUnit.Framework;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class NetworkInputSubmissionClockTests
    {
        [Test]
        public void ShouldSubmit_AtSixtyFramesPerSecond_SubmitsThirtyTimesPerSecond()
        {
            var clock = new NetworkInputSubmissionClock(30f);
            int submissionCount = 0;

            for (int frame = 0; frame < 60; ++frame)
            {
                if (clock.ShouldSubmit(1f / 60f))
                {
                    ++submissionCount;
                }
            }

            Assert.That(submissionCount, Is.EqualTo(30));
        }

        [Test]
        public void ShouldSubmit_AtOneHundredTwentyFramesPerSecond_PreservesRemainder()
        {
            var clock = new NetworkInputSubmissionClock(30f);
            int submissionCount = 0;

            for (int frame = 0; frame < 120; ++frame)
            {
                if (clock.ShouldSubmit(1f / 120f))
                {
                    ++submissionCount;
                }
            }

            Assert.That(submissionCount, Is.EqualTo(30));
        }

        [Test]
        public void ShouldSubmit_AfterLongFrame_DropsCatchUpDebt()
        {
            var clock = new NetworkInputSubmissionClock(30f);

            Assert.That(clock.ShouldSubmit(0.5f), Is.True);
            Assert.That(clock.ShouldSubmit(1f / 60f), Is.False);
            Assert.That(clock.ShouldSubmit(1f / 60f), Is.True);
        }

        [Test]
        public void Reset_DiscardsPartialInterval()
        {
            var clock = new NetworkInputSubmissionClock(30f);
            Assert.That(clock.ShouldSubmit(1f / 60f), Is.False);

            clock.Reset();

            Assert.That(clock.ShouldSubmit(1f / 60f), Is.False);
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void Constructor_WithInvalidRate_Throws(float submissionRateHz)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new NetworkInputSubmissionClock(submissionRateHz));
        }
    }
}
