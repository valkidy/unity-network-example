using System.Collections.Generic;
using System.Linq;
using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.Client
{
    /// <summary>
    /// Once-a-second log line for checking remote presentation in play (G0 of
    /// docs/REMOTE_PRESENTATION_NEXT_IMPLEMENTATION_PLAN.md in network-kernel).
    /// </summary>
    /// <remarks>
    /// The point is to tell two kinds of stop apart. A world freeze -- every
    /// remote thing that should be moving stops on the same frame -- is the
    /// snapshot stream running late, which the render clock is meant to cover.
    /// An entity freeze -- one thing stops while others move -- is a missing
    /// sample, which bridging should already cover. A jump is whichever of the two
    /// catching up. Only remote entities count: the local player and its own
    /// projectiles are predicted and never wait for a snapshot.
    ///
    /// Each report is followed by "[G0 frozen]" lines for the entities that
    /// stayed stopped longest and "[G0 jump]" lines for the jumps, with what
    /// they are, so a missing sample can be told from an agent pushing into a
    /// crowd (a velocity it cannot make good) or a teleport (a respawn, a pickup).
    /// </remarks>
    public sealed class RemotePresentationProbe
    {
        // Moving less than this between frames counts as not moving.
        private const float StillMeters = 0.001f;
        // An entity whose replicated horizontal speed is below this is not
        // expected to move. Horizontal only: a grounded agent has been seen
        // replicating a vertical velocity that grows with gravity while it
        // stands still, and the bridging extrapolates horizontally anyway.
        private const float MovingSpeed = 0.5f;
        // A step larger than this, and than three frames of its own speed, is a jump.
        private const float MinJumpMeters = 0.3f;
        private const int ReportedFrozen = 3;
        private const int ReportedJumps = 5;

        private struct Sample
        {
            public RenderEntityState State;
            public float Seconds;
            public Vector3 From;
            public float Step;
        }

        // How long each entity has been stopped while its speed said it should
        // move, without a break; carried across windows.
        private readonly Dictionary<uint, float> frozenFor = new Dictionary<uint, float>();
        // Per window: each entity's longest stop, as it stood at the end of it.
        private readonly Dictionary<uint, Sample> windowFrozen = new Dictionary<uint, Sample>();
        private readonly List<Sample> jumpSamples = new List<Sample>();

        private readonly Dictionary<uint, Vector3> previous = new Dictionary<uint, Vector3>();
        private readonly HashSet<uint> seen = new HashSet<uint>();
        private readonly List<uint> gone = new List<uint>();
        private readonly List<uint> frozenNow = new List<uint>();

        private float windowSeconds;
        private int frames;
        private int remoteMax;
        private int worldFreezeFrames;
        private float worldFreezeSeconds;
        private float longestWorldFreezeSeconds;
        private int entityFreezeFrames;
        private readonly HashSet<uint> frozenEntities = new HashSet<uint>();
        private int jumps;
        private float maxJumpMeters;
        private int staleActorFrameSum;
        private int staleActorMax;

        private bool hasPreviousStats;
        private ulong previousBudgetDropped;
        private ulong previousStaleDropped;

        public void Reset()
        {
            previous.Clear();
            frozenFor.Clear();
            hasPreviousStats = false;
            ResetWindow();
            worldFreezeSeconds = 0f;
        }

        public void Capture(RenderEntityState[] states, int count, uint localPlayerNetId, float deltaSeconds)
        {
            int movers = 0;
            int expected = 0;
            int staleActors = 0;
            int remote = 0;
            seen.Clear();
            frozenNow.Clear();
            for (int index = 0; index < count; ++index)
            {
                RenderEntityState state = states[index];
                if (state.net_id == 0 || state.net_id == localPlayerNetId ||
                    state.status == RenderEntityStatus.Predicted)
                {
                    continue;
                }
                ++remote;
                if (state.entity_type == KernelEntityType.Actor &&
                    state.status == RenderEntityStatus.Stale)
                {
                    ++staleActors;
                }
                seen.Add(state.net_id);
                Vector3 position = new Vector3(state.position.x, state.position.y, state.position.z);
                float speed = new Vector2(state.velocity.x, state.velocity.z).magnitude;
                if (!previous.TryGetValue(state.net_id, out Vector3 last))
                {
                    previous[state.net_id] = position;
                    continue;
                }
                previous[state.net_id] = position;
                float step = Vector3.Distance(position, last);
                bool moved = step > StillMeters;
                bool shouldMove = speed > MovingSpeed;
                if (moved) ++movers;
                if (shouldMove) ++expected;
                if (step > Mathf.Max(MinJumpMeters, speed * deltaSeconds * 3f))
                {
                    ++jumps;
                    maxJumpMeters = Mathf.Max(maxJumpMeters, step);
                    if (jumpSamples.Count < ReportedJumps)
                    {
                        jumpSamples.Add(new Sample { State = state, From = last, Step = step });
                    }
                }
                if (shouldMove && !moved)
                {
                    // Counted below once the frame's movers are known.
                    frozenNow.Add(state.net_id);
                    frozenFor.TryGetValue(state.net_id, out float stoppedFor);
                    stoppedFor += deltaSeconds;
                    frozenFor[state.net_id] = stoppedFor;
                    if (!windowFrozen.TryGetValue(state.net_id, out Sample longest) ||
                        stoppedFor >= longest.Seconds)
                    {
                        windowFrozen[state.net_id] = new Sample { State = state, Seconds = stoppedFor };
                    }
                }
                else
                {
                    frozenFor.Remove(state.net_id);
                }
            }

            gone.Clear();
            foreach (uint netId in previous.Keys)
            {
                if (!seen.Contains(netId)) gone.Add(netId);
            }
            foreach (uint netId in gone)
            {
                previous.Remove(netId);
                frozenFor.Remove(netId);
            }

            bool worldFrozen = expected >= 2 && movers == 0;
            if (worldFrozen)
            {
                ++worldFreezeFrames;
                worldFreezeSeconds += deltaSeconds;
                longestWorldFreezeSeconds = Mathf.Max(longestWorldFreezeSeconds, worldFreezeSeconds);
            }
            else
            {
                worldFreezeSeconds = 0f;
            }
            // A stop is only an entity freeze if something else moved this frame.
            if (movers > 0)
            {
                entityFreezeFrames += frozenNow.Count;
                frozenEntities.UnionWith(frozenNow);
            }

            ++frames;
            remoteMax = Mathf.Max(remoteMax, remote);
            staleActorFrameSum += staleActors;
            staleActorMax = Mathf.Max(staleActorMax, staleActors);
            windowSeconds += deltaSeconds;
        }

        /// <summary>Logs and restarts the window once it is at least <paramref name="intervalSeconds"/> long.</summary>
        public void ReportIfDue(NetworkExample.Kernel.Kernel kernel, float intervalSeconds)
        {
            if (windowSeconds < intervalSeconds || frames == 0)
            {
                return;
            }

            string network = "stats=unavailable";
            if (kernel != null && kernel.TryGetNetworkStats(out KernelNetworkStats stats))
            {
                ulong budgetDelta = hasPreviousStats
                    ? stats.remote_presentation_budget_dropped - previousBudgetDropped
                    : 0ul;
                ulong staleDelta = hasPreviousStats
                    ? stats.remote_presentation_stale_dropped - previousStaleDropped
                    : 0ul;
                previousBudgetDropped = stats.remote_presentation_budget_dropped;
                previousStaleDropped = stats.remote_presentation_stale_dropped;
                hasPreviousStats = true;
                network =
                    $"rtt={stats.rtt_us / 1000.0:F1}ms jitter={stats.jitter_us / 1000.0:F1}ms " +
                    $"loss={stats.loss_ratio:P1} presBudgetDropped=+{budgetDelta} " +
                    $"presStaleDropped=+{staleDelta}";
            }

            Debug.Log(
                $"[G0] {windowSeconds:F1}s frames={frames} remote={remoteMax} " +
                $"worldFreezeFrames={worldFreezeFrames} " +
                $"longestWorldFreeze={longestWorldFreezeSeconds * 1000f:F0}ms " +
                $"entityFreezeFrames={entityFreezeFrames} frozenEntities={frozenEntities.Count} " +
                $"jumps={jumps} maxJump={maxJumpMeters:F2}m " +
                $"staleActors avg={(float)staleActorFrameSum / frames:F1} max={staleActorMax} " +
                network);
            foreach (Sample frozen in windowFrozen.Values
                         .OrderByDescending(sample => sample.Seconds)
                         .Take(ReportedFrozen))
            {
                Debug.Log(
                    $"[G0 frozen] {Describe(frozen.State)} stopped={frozen.Seconds * 1000f:F0}ms " +
                    $"at={Format(frozen.State.position)}");
            }
            foreach (Sample jump in jumpSamples)
            {
                Debug.Log(
                    $"[G0 jump] {Describe(jump.State)} step={jump.Step:F2}m " +
                    $"from={Format(jump.From)} to={Format(jump.State.position)}");
            }
            ResetWindow();
        }

        private static string Describe(RenderEntityState state)
        {
            float speed = new Vector3(state.velocity.x, state.velocity.y, state.velocity.z).magnitude;
            bool dead = (state.visual_flags & KernelConstants.VisualFlagDead) != 0;
            return
                $"net_id={state.net_id} type={state.entity_type}/{state.actor_type} " +
                $"template={state.template_id} status={state.status} " +
                $"speed={speed:F2} velocity={Format(state.velocity)} dead={dead} " +
                $"flags=0x{state.visual_flags:X} anim={state.animation_state} " +
                $"item_mode={state.world_item_mode} carrier={state.carrier_entity_id}";
        }

        private static string Format(KernelVec3 value)
        {
            return $"({value.x:F2}, {value.y:F2}, {value.z:F2})";
        }

        private static string Format(Vector3 value)
        {
            return $"({value.x:F2}, {value.y:F2}, {value.z:F2})";
        }

        private void ResetWindow()
        {
            windowSeconds = 0f;
            frames = 0;
            remoteMax = 0;
            worldFreezeFrames = 0;
            longestWorldFreezeSeconds = 0f;
            entityFreezeFrames = 0;
            frozenEntities.Clear();
            windowFrozen.Clear();
            jumpSamples.Clear();
            jumps = 0;
            maxJumpMeters = 0f;
            staleActorFrameSum = 0;
            staleActorMax = 0;
        }
    }
}
