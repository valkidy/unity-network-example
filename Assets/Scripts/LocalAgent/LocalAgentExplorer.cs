using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkExample.UnityDemo.LocalAgent
{
    /// <summary>
    /// Frontier exploration over a navigation mesh: the walkable area is split
    /// into square cells, cells near the agent are marked seen as it moves, and it
    /// walks a navmesh path to the nearest cell it has not seen yet.
    /// </summary>
    /// <remarks>
    /// Nearest-unseen is what makes this frontier exploration rather than a random
    /// walk: the seen region grows around the agent, so its nearest unseen cell is
    /// always on that region's edge. "Seen" is a sight radius with no line-of-sight
    /// test. A target the agent cannot reach -- no navmesh route, or no progress
    /// for <see cref="StuckSteps"/> steps because something the navmesh does not
    /// know about is in the way -- is set aside until the area is explored, at
    /// which point the area is forgotten and exploration starts over, so the agent
    /// keeps patrolling instead of stopping. Steps are the agent's input ticks.
    /// </remarks>
    public sealed class LocalAgentExplorer
    {
        private const int MaxPlanAttemptsPerStep = 8;
        private const float ProgressDistance = 0.5f;

        private readonly DetourNavMeshQuery query;
        private readonly float cellSize;
        private readonly int columns;
        private readonly int rows;
        private readonly Vector3 origin;
        private readonly Vector3[] cellPoints;
        private readonly bool[] walkable;
        private readonly bool[] seen;
        private readonly bool[] setAside;
        private readonly List<Vector3> corners = new List<Vector3>();
        private int cornerIndex;
        private Vector3 progressAnchor;
        private int stepsWithoutProgress;

        public LocalAgentExplorer(DetourNavMeshQuery query, float cellSize)
        {
            this.query = query ?? throw new ArgumentNullException(nameof(query));
            this.cellSize = Mathf.Max(0.5f, float.IsFinite(cellSize) ? cellSize : 4f);
            DetourNavMesh mesh = query.Mesh;
            origin = mesh.BoundsMin;
            columns = Mathf.Max(1, Mathf.CeilToInt((mesh.BoundsMax.x - origin.x) / this.cellSize));
            rows = Mathf.Max(1, Mathf.CeilToInt((mesh.BoundsMax.z - origin.z) / this.cellSize));
            cellPoints = new Vector3[columns * rows];
            walkable = new bool[cellPoints.Length];
            seen = new bool[cellPoints.Length];
            setAside = new bool[cellPoints.Length];
            float midHeight = (mesh.BoundsMin.y + mesh.BoundsMax.y) * 0.5f;
            for (int cell = 0; cell < cellPoints.Length; cell++)
            {
                var center = new Vector3(origin.x + (cell % columns + 0.5f) * this.cellSize, midHeight,
                    origin.z + (cell / columns + 0.5f) * this.cellSize);
                // A cell counts only if its centre is walkable; the rim of the mesh is
                // narrower than a cell and is seen from the cells beside it anyway.
                if (query.FindPolygon(center) < 0) continue;
                walkable[cell] = true;
                cellPoints[cell] = query.ClosestPoint(center, out _);
                WalkableCellCount++;
            }
        }

        public DetourNavMeshQuery Query => query;
        public float CellSize => cellSize;
        public int CellCount => cellPoints.Length;
        public int WalkableCellCount { get; }
        public int SeenCellCount { get; private set; }
        /// <summary>The cell being walked to, or -1.</summary>
        public int TargetCell { get; private set; } = -1;
        public Vector3 TargetPoint => TargetCell >= 0 ? cellPoints[TargetCell] : Vector3.zero;
        public IReadOnlyList<Vector3> Path => corners;
        public int SetAsideCount { get; private set; }
        public int ForgottenCount { get; private set; }

        public int StuckSteps { get; set; } = 45;
        public float WaypointReachDistance { get; set; } = 0.75f;

        public bool IsSeen(int cell) => seen[cell];
        public bool IsWalkable(int cell) => walkable[cell];
        public Vector3 GetCellPoint(int cell) => cellPoints[cell];
        public int CellAt(Vector3 point)
        {
            int column = Mathf.FloorToInt((point.x - origin.x) / cellSize);
            int row = Mathf.FloorToInt((point.z - origin.z) / cellSize);
            return column < 0 || row < 0 || column >= columns || row >= rows ? -1 : row * columns + column;
        }

        /// <summary>Marks every walkable cell whose centre is within <paramref name="radius"/>.</summary>
        public void MarkSeen(Vector3 position, float radius)
        {
            radius = Mathf.Max(0f, radius);
            int reach = Mathf.CeilToInt(radius / cellSize) + 1;
            int centerColumn = Mathf.FloorToInt((position.x - origin.x) / cellSize);
            int centerRow = Mathf.FloorToInt((position.z - origin.z) / cellSize);
            for (int row = Mathf.Max(0, centerRow - reach); row <= Mathf.Min(rows - 1, centerRow + reach); row++)
            for (int column = Mathf.Max(0, centerColumn - reach); column <= Mathf.Min(columns - 1, centerColumn + reach); column++)
            {
                int cell = row * columns + column;
                if (walkable[cell] && DistanceXZ(cellPoints[cell], position) <= radius)
                    MarkCellSeen(cell);
            }
        }

        /// <summary>Drops the current target and path; seen cells are kept.</summary>
        public void ClearPath()
        {
            TargetCell = -1;
            corners.Clear();
            cornerIndex = 0;
            stepsWithoutProgress = 0;
        }

        /// <summary>
        /// Advances one input tick and returns the ground-plane direction to walk,
        /// or zero while no target is available. With an <paramref name="anchor"/>,
        /// only cells within <paramref name="leash"/> of it are explored.
        /// </summary>
        public Vector2 Step(Vector3 position, Vector3? anchor, float leash)
        {
            if (TargetCell >= 0 && anchor.HasValue && DistanceXZ(cellPoints[TargetCell], anchor.Value) > leash)
                ClearPath(); // the anchor moved away from it

            if (TargetCell >= 0)
            {
                if (DistanceXZ(position, progressAnchor) >= ProgressDistance)
                {
                    progressAnchor = position;
                    stepsWithoutProgress = 0;
                }
                else if (++stepsWithoutProgress >= Mathf.Max(1, StuckSteps))
                {
                    SetAside(TargetCell);
                    ClearPath();
                }
            }

            while (TargetCell >= 0 && cornerIndex < corners.Count &&
                DistanceXZ(position, corners[cornerIndex]) <= WaypointReachDistance)
                cornerIndex++;
            if (TargetCell >= 0 && cornerIndex >= corners.Count)
            {
                MarkCellSeen(TargetCell);
                ClearPath();
            }

            if (TargetCell < 0 && !TryChooseTarget(position, anchor, leash))
                return Vector2.zero;

            Vector3 next = corners[cornerIndex];
            var direction = new Vector2(next.x - position.x, next.z - position.z);
            return direction.sqrMagnitude > 1e-8f ? direction.normalized : Vector2.zero;
        }

        private void MarkCellSeen(int cell)
        {
            if (seen[cell]) return;
            seen[cell] = true;
            SeenCellCount++;
        }

        private bool TryChooseTarget(Vector3 position, Vector3? anchor, float leash)
        {
            for (int attempt = 0; attempt < MaxPlanAttemptsPerStep; attempt++)
            {
                int best = -1;
                float bestDistance = float.PositiveInfinity;
                bool anyInArea = false;
                for (int cell = 0; cell < cellPoints.Length; cell++)
                {
                    if (!walkable[cell] || !InArea(cell, anchor, leash)) continue;
                    anyInArea = true;
                    if (seen[cell] || setAside[cell]) continue;
                    float distance = DistanceXZ(cellPoints[cell], position);
                    if (distance < bestDistance)
                    {
                        best = cell;
                        bestDistance = distance;
                    }
                }
                if (best < 0)
                {
                    // Everything here is seen or unreachable: start the area over,
                    // keeping what is in sight now so the agent turns outward.
                    if (anyInArea) Forget(anchor, leash);
                    return false;
                }
                if (query.FindPath(position, cellPoints[best], corners) && corners.Count >= 2)
                {
                    TargetCell = best;
                    cornerIndex = 1;
                    progressAnchor = position;
                    stepsWithoutProgress = 0;
                    return true;
                }
                SetAside(best);
            }
            return false;
        }

        private void SetAside(int cell)
        {
            if (setAside[cell]) return;
            setAside[cell] = true;
            SetAsideCount++;
        }

        private void Forget(Vector3? anchor, float leash)
        {
            for (int cell = 0; cell < cellPoints.Length; cell++)
            {
                if (!walkable[cell] || !InArea(cell, anchor, leash)) continue;
                if (seen[cell]) SeenCellCount--;
                seen[cell] = false;
                if (setAside[cell]) SetAsideCount--;
                setAside[cell] = false;
            }
            ForgottenCount++;
        }

        private bool InArea(int cell, Vector3? anchor, float leash) =>
            !anchor.HasValue || DistanceXZ(cellPoints[cell], anchor.Value) <= leash;

        private static float DistanceXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
