using NetworkExample.UnityDemo.UI;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class ShelterRestViewTests
    {
        [TestCase(1000u, 5400u, 1000u, 5400u)]
        [TestCase(1000u, 5400u, 3700u, 2700u)]
        [TestCase(1000u, 5400u, 6400u, 0u)]
        [TestCase(1000u, 5400u, 9000u, 0u)]
        // The tick counter wrapped between the spawn and now.
        [TestCase(4294967000u, 5400u, 104u, 5000u)]
        public void RemainingTicks_CountsDownFromSpawnAndStopsAtZero(
            uint spawnTick,
            uint lifetimeTicks,
            uint currentTick,
            uint expected)
        {
            Assert.That(
                ShelterRestView.RemainingTicks(spawnTick, lifetimeTicks, currentTick),
                Is.EqualTo(expected));
        }

        [TestCase(5400u, "3:00")]
        [TestCase(3750u, "2:05")]
        [TestCase(1u, "0:01")]
        [TestCase(0u, "0:00")]
        public void FormatCountdown_RoundsUpToWholeSeconds(uint remainingTicks, string expected)
        {
            Assert.That(ShelterRestView.FormatCountdown(remainingTicks, 30), Is.EqualTo(expected));
        }

        [Test]
        public void OpenAndClose_ToggleThePanel()
        {
            var host = new GameObject("ShelterRestViewTests");
            try
            {
                ShelterRestView view = host.AddComponent<ShelterRestView>();
                Assert.That(view.IsOpen, Is.False);

                view.Open(ShelterRestView.RestUiId);
                view.SetStatus(true, true, 3750, 30, 300, 600);
                Assert.That(view.IsOpen, Is.True);

                view.Close();
                Assert.That(view.IsOpen, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }
    }
}
