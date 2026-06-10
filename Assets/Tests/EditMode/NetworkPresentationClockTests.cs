using System.IO;
using NetworkExample.UnityDemo.Common;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class NetworkPresentationClockTests
    {
        [Test]
        public void Advance_WithSixteenMillisecondFrame_ReturnsMicroseconds()
        {
            var clock = new NetworkPresentationClock();

            ulong timeUs = clock.Advance(0.016f);

            Assert.That(timeUs, Is.InRange(15999UL, 16001UL));
        }

        [Test]
        public void Advance_WithMultiplePositiveDeltas_IsMonotonicAndAccumulates()
        {
            var clock = new NetworkPresentationClock();

            ulong first = clock.Advance(0.25f);
            ulong second = clock.Advance(0.5f);

            Assert.That(first, Is.EqualTo(250000UL));
            Assert.That(second, Is.EqualTo(750000UL));
            Assert.That(second, Is.GreaterThan(first));
        }

        [Test]
        public void Advance_WithZeroOrNegativeDelta_DoesNotChangeTime()
        {
            var clock = new NetworkPresentationClock();

            ulong before = clock.Advance(0.5f);
            ulong zeroDelta = clock.Advance(0f);
            ulong negativeDelta = clock.Advance(-0.25f);

            Assert.That(zeroDelta, Is.EqualTo(before));
            Assert.That(negativeDelta, Is.EqualTo(before));
        }

        [Test]
        public void Reset_ReturnsClockToZeroOrigin()
        {
            var clock = new NetworkPresentationClock();
            clock.Advance(1f);

            clock.Reset();
            ulong afterReset = clock.Advance(0.25f);

            Assert.That(afterReset, Is.EqualTo(250000UL));
        }

        [Test]
        public void Runners_UseRenderStatesAtPresentationTime()
        {
            string clientRunner = ReadAssetText("Scripts/Client/ClientRunner.cs");
            string hostModeRunner = ReadAssetText("Scripts/Host/HostModeRunner.cs");

            StringAssert.Contains(".GetRenderStatesAtTime(clientRenderTimeUs, renderStates)", clientRunner);
            StringAssert.Contains(".GetRenderStatesAtTime(clientRenderTimeUs, renderStates)", hostModeRunner);
            StringAssert.DoesNotContain(".GetRenderStates(renderStates)", clientRunner);
            StringAssert.DoesNotContain(".GetRenderStates(renderStates)", hostModeRunner);
        }

        [Test]
        public void HostModeRunner_ForwardsInitialLocalPlayerJoinToGameServer()
        {
            string hostModeRunner = ReadAssetText("Scripts/Host/HostModeRunner.cs");

            StringAssert.Contains("type = KernelEventType.PlayerJoined", hostModeRunner);
            StringAssert.Contains("host.GameServer.HandleEvent", hostModeRunner);
        }

        private static string ReadAssetText(string projectRelativePath)
        {
            string path = Path.Combine(Application.dataPath, projectRelativePath);
            return File.ReadAllText(path);
        }
    }
}
