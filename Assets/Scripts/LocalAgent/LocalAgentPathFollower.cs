using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkExample.UnityDemo.LocalAgent
{
    public enum PathProgress { None, Walking, Arrived, Stuck }

    /// <summary>
    /// Walks a navmesh path corner by corner, and notices when the agent stops
    /// making progress along it. Steps are the agent's input ticks.
    /// </summary>
    public sealed class LocalAgentPathFollower
    {
        private const float ProgressDistance = 0.5f;

        private readonly DetourNavMeshQuery query;
        private readonly List<Vector3> corners = new List<Vector3>();
        private int cornerIndex;
        private Vector3 progressAnchor;
        private int stepsWithoutProgress;

        public LocalAgentPathFollower(DetourNavMeshQuery query) =>
            this.query = query ?? throw new ArgumentNullException(nameof(query));

        public DetourNavMeshQuery Query => query;
        public bool HasPath => corners.Count > 0;
        /// <summary>The end of the path; meaningful only while <see cref="HasPath"/>.</summary>
        public Vector3 Destination => corners.Count > 0 ? corners[corners.Count - 1] : Vector3.zero;
        public IReadOnlyList<Vector3> Path => corners;
        /// <summary>Steps without 0.5 m of progress before the path counts as stuck.</summary>
        public int StuckSteps { get; set; } = 45;
        public float WaypointReachDistance { get; set; } = 0.75f;

        /// <summary>Plans a path; false (and no path) when the navmesh has no route.</summary>
        public bool TryPlan(Vector3 from, Vector3 to)
        {
            Clear();
            if (!query.FindPath(from, to, corners) || corners.Count < 2)
            {
                corners.Clear();
                return false;
            }
            cornerIndex = 1;
            progressAnchor = from;
            return true;
        }

        public void Clear()
        {
            corners.Clear();
            cornerIndex = 0;
            stepsWithoutProgress = 0;
        }

        /// <summary>
        /// Advances one step. Walking gives the ground-plane direction to the next
        /// corner; Arrived and Stuck clear the path.
        /// </summary>
        public PathProgress Step(Vector3 position, out Vector2 direction)
        {
            direction = Vector2.zero;
            if (corners.Count == 0) return PathProgress.None;
            if (DistanceXZ(position, progressAnchor) >= ProgressDistance)
            {
                progressAnchor = position;
                stepsWithoutProgress = 0;
            }
            else if (++stepsWithoutProgress >= Mathf.Max(1, StuckSteps))
            {
                Clear();
                return PathProgress.Stuck;
            }
            while (cornerIndex < corners.Count && DistanceXZ(position, corners[cornerIndex]) <= WaypointReachDistance)
                cornerIndex++;
            if (cornerIndex >= corners.Count)
            {
                Clear();
                return PathProgress.Arrived;
            }
            direction = Direction(position);
            return PathProgress.Walking;
        }

        /// <summary>The ground-plane direction to the next corner, without advancing.</summary>
        public Vector2 Direction(Vector3 position)
        {
            if (cornerIndex >= corners.Count) return Vector2.zero;
            Vector3 next = corners[cornerIndex];
            var toward = new Vector2(next.x - position.x, next.z - position.z);
            return toward.sqrMagnitude > 1e-8f ? toward.normalized : Vector2.zero;
        }

        internal static float DistanceXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
