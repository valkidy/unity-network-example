using NetworkExample.UnityDemo.Common;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    /// <summary>
    /// Covers the patrol circuit LocomotionTest.unity is authored with. The walk
    /// here is the ideal one -- the subject turns instantly and moves exactly at
    /// monster_sim_actor's 2.5 m/s -- so it checks what the path script asks for,
    /// not what the kernel's yaw-rate-limited body does with it. The scene's
    /// patrol-bounds guard is what absorbs the difference.
    /// </summary>
    public sealed class NetworkLocomotionPathScriptTests
    {
        private const string PatrolPath = "+X; forward 30m; -X; forward 60m; +X; forward 30m";
        private const float TickRateHz = 30f;
        private const float MoveSpeedMetersPerSecond = 2.5f;

        [Test]
        public void PatrolPath_WalksOutAndBack_AndEndsWhereItStarted()
        {
            NetworkLocomotionPathScript path = Parse(PatrolPath);

            Vector3 start = Vector3.zero;
            Vector3 end = Walk(path, start, 10000, out Vector3 min, out Vector3 max);

            Assert.That(path.Finished, Is.True, "patrol did not finish inside the tick budget");
            Assert.That(max.x, Is.EqualTo(30f).Within(0.2f), "outbound leg did not reach +30 m");
            Assert.That(min.x, Is.EqualTo(-30f).Within(0.2f), "return leg did not reach -30 m");
            Assert.That(
                Vector3.Distance(end, start),
                Is.LessThan(0.2f),
                "patrol is not a closed circuit: ended at " + end.ToString("F3"));
        }

        [Test]
        public void RestartedPatrol_StaysInsideTheTerrain_OverManyLaps()
        {
            NetworkLocomotionPathScript path = Parse(PatrolPath);

            // Each lap is ~120 m at 2.5 m/s; the tick budget leaves plenty of slack.
            // A Forward step ends on the first tick that crosses its distance, so
            // every leg overshoots by up to one tick of travel (0.083 m) and a
            // replayed lap starts a little further out than the last one.
            Vector3 position = Vector3.zero;
            var min = Vector3.zero;
            var max = Vector3.zero;
            int laps = 0;
            for (int lap = 0; lap < 10; ++lap)
            {
                position = Walk(position, path, 10000, ref min, ref max);
                Assert.That(path.Finished, Is.True, "lap " + lap + " never finished");
                path.Restart();
                ++laps;
            }

            Assert.That(laps, Is.EqualTo(10));
            // The scene's guard sits at +/-35 m and the terrain edge at +/-49 m.
            Assert.That(max.x, Is.LessThan(35f), "patrol drifted past the +X bound");
            Assert.That(min.x, Is.GreaterThan(-35f), "patrol drifted past the -X bound");
            Assert.That(
                Mathf.Max(Mathf.Abs(min.z), Mathf.Abs(max.z)),
                Is.LessThan(1f),
                "patrol left the Z lane it was authored on");
        }

        [Test]
        public void Restart_ReplaysFromTheFirstStep()
        {
            NetworkLocomotionPathScript path = Parse("+Z; forward 5m; turn 90; forward 5m");

            Vector2 first = path.MoveInput(Vector3.zero);
            Walk(path, Vector3.zero, 10000, out _, out _);
            Assert.That(path.Finished, Is.True);
            Assert.That(path.MoveInput(Vector3.zero), Is.EqualTo(Vector2.zero));

            path.Restart();

            Assert.That(path.Finished, Is.False, "Restart left the path finished");
            Assert.That(
                path.MoveInput(Vector3.zero),
                Is.EqualTo(first),
                "Restart did not restore the path's initial heading");
        }

        [Test]
        public void BareAxis_StillRunsUnbounded()
        {
            NetworkLocomotionPathScript path = Parse("+X");

            Vector3 end = Walk(path, Vector3.zero, 600, out _, out _);

            Assert.That(path.Finished, Is.False, "a bare axis must never finish");
            Assert.That(end.x, Is.GreaterThan(45f), "bare +X did not walk the full 20 s");
        }

        private static NetworkLocomotionPathScript Parse(string text)
        {
            Assert.That(
                NetworkLocomotionPathScript.TryParse(
                    text,
                    TickRateHz,
                    out NetworkLocomotionPathScript path,
                    out string error),
                Is.True,
                error);
            return path;
        }

        private static Vector3 Walk(
            NetworkLocomotionPathScript path,
            Vector3 start,
            int maxTicks,
            out Vector3 min,
            out Vector3 max)
        {
            min = start;
            max = start;
            return Walk(start, path, maxTicks, ref min, ref max);
        }

        private static Vector3 Walk(
            Vector3 start,
            NetworkLocomotionPathScript path,
            int maxTicks,
            ref Vector3 min,
            ref Vector3 max)
        {
            const float step = MoveSpeedMetersPerSecond / TickRateHz;
            Vector3 position = start;
            for (int tick = 0; tick < maxTicks && !path.Finished; ++tick)
            {
                Vector2 move = path.MoveInput(position);
                position += new Vector3(move.x, 0f, move.y) * step;
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }
            return position;
        }
    }
}
