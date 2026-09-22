using System.Collections.Generic;
using NetworkExample.Kernel;
using NetworkExample.UnityDemo.LocalAgent;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class LocalAgentExplorerTests
    {
        // 5 m/s at the default 30 Hz input rate, as measured against a live server.
        private const float StepDistance = 5f / 30f;
        private const float Sight = 6f;

        private static float DistanceXZ(Vector3 a, Vector3 b) =>
            new Vector2(a.x - b.x, a.z - b.z).magnitude;

        private static int CellsWithin(LocalAgentExplorer explorer, Vector3 anchor, float leash)
        {
            int count = 0;
            for (int cell = 0; cell < explorer.CellCount; cell++)
                if (explorer.IsWalkable(cell) && DistanceXZ(explorer.GetCellPoint(cell), anchor) <= leash) count++;
            return count;
        }

        // Walks the agent the way the server would: along the command, kept on the mesh.
        private static Vector3 Walk(LocalAgentExplorer explorer, Vector3 position, Vector2 move) =>
            explorer.Query.ClosestPoint(position + new Vector3(move.x, 0f, move.y) * StepDistance, out _);

        [Test]
        public void SeesEveryCellInTheLeashWithoutRetracingMuch()
        {
            var explorer = new LocalAgentExplorer(DetourNavMeshTests.Plane(), 4f);
            Vector3 anchor = Vector3.zero, position = Vector3.zero;
            const float leash = 20f;
            int area = CellsWithin(explorer, anchor, leash);
            float walked = 0f;
            int steps = 0;
            for (; steps < 3000 && explorer.SeenCellCount < area && explorer.ForgottenCount == 0; steps++)
            {
                explorer.MarkSeen(position, Sight);
                Vector2 move = explorer.Step(position, anchor, leash);
                if (explorer.TargetCell >= 0)
                    Assert.That(DistanceXZ(explorer.TargetPoint, anchor), Is.LessThanOrEqualTo(leash));
                Vector3 next = Walk(explorer, position, move);
                walked += DistanceXZ(position, next);
                position = next;
            }
            // A 12 m wide sweep over the ~1,250 m2 disc is about 105 m; allow three times that.
            Assert.That(explorer.SeenCellCount, Is.EqualTo(area), "after " + steps + " steps");
            Assert.That(walked, Is.LessThan(315f), "walked " + walked + " m in " + steps + " steps");
            Assert.That(explorer.SetAsideCount, Is.Zero, "open ground has nothing to get stuck on");
        }

        [Test]
        public void WithoutAnAnchorItHeadsForTheNearestUnseenCell()
        {
            var explorer = new LocalAgentExplorer(DetourNavMeshTests.Plane(), 4f);
            var position = new Vector3(0.5f, 0f, 0.5f);
            explorer.MarkSeen(position, Sight);
            Vector2 move = explorer.Step(position, null, 0f);
            Assert.That(move.magnitude, Is.EqualTo(1f).Within(1e-4f));
            float nearestUnseen = float.PositiveInfinity;
            for (int cell = 0; cell < explorer.CellCount; cell++)
                if (explorer.IsWalkable(cell) && !explorer.IsSeen(cell))
                    nearestUnseen = Mathf.Min(nearestUnseen, DistanceXZ(explorer.GetCellPoint(cell), position));
            Assert.That(DistanceXZ(explorer.TargetPoint, position), Is.EqualTo(nearestUnseen).Within(1e-4f));
            Assert.That(DistanceXZ(explorer.TargetPoint, position), Is.GreaterThan(Sight));
        }

        [Test]
        public void ATargetItMakesNoProgressTowardsIsSetAside()
        {
            var explorer = new LocalAgentExplorer(DetourNavMeshTests.Plane(), 4f) { StuckSteps = 10 };
            Vector3 position = Vector3.zero; // something the navmesh does not know about holds it here
            explorer.MarkSeen(position, Sight);
            explorer.Step(position, null, 0f);
            int blocked = explorer.TargetCell;
            for (int step = 0; step < 9; step++) explorer.Step(position, null, 0f);
            Assert.That(explorer.TargetCell, Is.EqualTo(blocked), "not yet");
            explorer.Step(position, null, 0f);
            Assert.That(explorer.SetAsideCount, Is.EqualTo(1));
            Assert.That(explorer.TargetCell, Is.Not.EqualTo(blocked).And.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void CellsOnAnotherIslandAreSetAsideThenTheAreaStartsOver()
        {
            DetourNavMeshQuery query = DetourNavMeshTests.Undulating();
            var explorer = new LocalAgentExplorer(query, 4f);
            Vector3 position = query.Mesh.GetPolygonCenter(0);
            var reachable = new List<Vector3>();
            // See every cell the agent can walk to, leaving only other islands unseen.
            for (int cell = 0; cell < explorer.CellCount; cell++)
            {
                if (!explorer.IsWalkable(cell)) continue;
                if (query.FindPath(position, explorer.GetCellPoint(cell), reachable))
                    explorer.MarkSeen(explorer.GetCellPoint(cell), 0f);
            }
            int unreachable = explorer.WalkableCellCount - explorer.SeenCellCount;
            if (unreachable == 0) Assert.Ignore("undulating is one connected region");

            for (int step = 0; step < unreachable && explorer.ForgottenCount == 0; step++)
            {
                Assert.That(explorer.Step(position, null, 0f), Is.EqualTo(Vector2.zero));
                Assert.That(explorer.TargetCell, Is.EqualTo(-1));
            }
            Assert.That(explorer.ForgottenCount, Is.EqualTo(1), "everything reachable was seen");
            Assert.That(explorer.SeenCellCount, Is.Zero);
            Assert.That(explorer.Step(position, null, 0f).magnitude, Is.EqualTo(1f).Within(1e-4f),
                "and it sets off again");
        }

        [Test]
        public void ATargetTheAnchorMovesAwayFromIsDropped()
        {
            var explorer = new LocalAgentExplorer(DetourNavMeshTests.Plane(), 4f);
            Vector3 position = Vector3.zero;
            explorer.MarkSeen(position, Sight);
            explorer.Step(position, Vector3.zero, 12f);
            Assert.That(DistanceXZ(explorer.TargetPoint, Vector3.zero), Is.LessThanOrEqualTo(12f));
            var moved = new Vector3(40f, 0f, 0f);
            explorer.Step(position, moved, 12f);
            Assert.That(DistanceXZ(explorer.TargetPoint, moved), Is.LessThanOrEqualTo(12f));
        }

        [Test]
        public void AnAreaSmallerThanTheSightRadiusLeavesNothingToWalkTo()
        {
            var explorer = new LocalAgentExplorer(DetourNavMeshTests.Plane(), 4f);
            explorer.MarkSeen(Vector3.zero, Sight);
            Assert.That(explorer.Step(Vector3.zero, Vector3.zero, 4f), Is.EqualTo(Vector2.zero));
            Assert.That(explorer.ForgottenCount, Is.EqualTo(1));
        }

        // --- controller integration ---

        private static RenderEntityState Actor(uint id, float x, KernelActorType type = KernelActorType.Player) =>
            new RenderEntityState { net_id = id, entity_type = KernelEntityType.Actor,
                actor_type = type, hp = 100, template_id = 100, position = new KernelVec3(x, 0, 0) };

        private static LocalAgentCommand Step(LocalAgentController controller, LocalAgentSettings settings,
            params RenderEntityState[] states)
        {
            var perception = new LocalAgentPerception(); // Omniscient: sees the render states as they are
            perception.Update(settings, 0f, states, states.Length, 1);
            return controller.Step(settings, perception, true,
                new KernelLocalWeaponState { weapon_id = 0, ammo = 10,
                    flags = KernelConstants.LocalWeaponStateFlagWeaponIdValid }, 0f);
        }

        [Test]
        public void AloneTheAgentExploresAndWithoutANavMeshItStaysPut()
        {
            var settings = new LocalAgentSettings { enemyTemplateIds = new uint[] { 100 } };
            var controller = new LocalAgentController();
            Assert.That(Step(controller, settings, Actor(1, 0)).Move, Is.EqualTo(Vector2.zero));
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Idle));

            controller.NavMesh = DetourNavMeshTests.Plane();
            Assert.That(Step(controller, settings, Actor(1, 0)).Move.magnitude, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Exploring));

            settings.enableExploration = false;
            Assert.That(Step(controller, settings, Actor(1, 0)).Move, Is.EqualTo(Vector2.zero));
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Idle));
        }

        [Test]
        public void ExploresWithinTheLeashAndComesBackBeyondIt()
        {
            var settings = new LocalAgentSettings { leashRadius = 15f, followStopDistance = 3f };
            var controller = new LocalAgentController { NavMesh = DetourNavMeshTests.Plane() };
            Step(controller, settings, Actor(1, 0), Actor(2, 10));
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Exploring));
            Assert.That(DistanceXZ(controller.Explorer.TargetPoint, new Vector3(10f, 0f, 0f)),
                Is.LessThanOrEqualTo(15f), "explores around the player it follows");

            var back = Step(controller, settings, Actor(1, 0), Actor(2, 20));
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Following));
            Assert.That(back.Move, Is.EqualTo(Vector2.right));
            Step(controller, settings, Actor(1, 0), Actor(2, 10));
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Following), "until it is close again");
            Step(controller, settings, Actor(1, 0), Actor(2, 2.5f));
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Exploring));
        }

        [Test]
        public void AnEnemyInterruptsExploration()
        {
            var settings = new LocalAgentSettings { enemyTemplateIds = new uint[] { 100 } };
            var controller = new LocalAgentController { NavMesh = DetourNavMeshTests.Plane() };
            Step(controller, settings, Actor(1, 0));
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Exploring));
            var command = Step(controller, settings, Actor(1, 0), Actor(3, 5, KernelActorType.Agent));
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Combat));
            Assert.That(command.Move, Is.EqualTo(Vector2.zero));
            Assert.That(command.Fire, Is.True);
            Step(controller, settings, Actor(1, 0));
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Exploring));
        }
    
        // --- path follower ---

        [Test]
        public void PathFollowerWalksToTheEndAndReportsArrival()
        {
            var path = new LocalAgentPathFollower(DetourNavMeshTests.Plane());
            Assert.That(path.Step(Vector3.zero, out _), Is.EqualTo(PathProgress.None));
            Assert.That(path.TryPlan(Vector3.zero, new Vector3(3, 0, 0)), Is.True);
            Assert.That(path.Destination.x, Is.EqualTo(3f).Within(0.01f));
            Assert.That(path.Direction(Vector3.zero), Is.EqualTo(Vector2.right));
            Vector3 position = Vector3.zero;
            PathProgress progress = PathProgress.Walking;
            int steps = 0;
            for (; steps < 100 && progress == PathProgress.Walking; steps++)
            {
                progress = path.Step(position, out Vector2 move);
                position = Walk(path.Query, position, move);
            }
            Assert.That(progress, Is.EqualTo(PathProgress.Arrived));
            Assert.That(path.HasPath, Is.False);
            Assert.That(steps, Is.LessThan(3f / StepDistance + 3));
        }

        [Test]
        public void PathFollowerReportsStuckWithoutProgress()
        {
            var path = new LocalAgentPathFollower(DetourNavMeshTests.Plane()) { StuckSteps = 5 };
            Assert.That(path.TryPlan(Vector3.zero, new Vector3(10, 0, 0)), Is.True);
            for (int i = 0; i < 4; i++)
                Assert.That(path.Step(Vector3.zero, out _), Is.EqualTo(PathProgress.Walking));
            Assert.That(path.Step(Vector3.zero, out _), Is.EqualTo(PathProgress.Stuck));
            Assert.That(path.HasPath, Is.False);
            Assert.That(path.TryPlan(Vector3.zero, new Vector3(500, 0, 0)), Is.True);
            Assert.That(path.Destination.x, Is.LessThanOrEqualTo(path.Query.Mesh.BoundsMax.x + 0.01f), "an end off the mesh is moved onto it");
        }

        private static Vector3 Walk(DetourNavMeshQuery query, Vector3 position, Vector2 move) =>
            query.ClosestPoint(position + new Vector3(move.x, 0f, move.y) * StepDistance, out _);

        // --- sight-limited exploration ---

        [Test]
        public void MarkSeenWithSightMarksOnlyVisibleCellsWithinABudget()
        {
            var explorer = new LocalAgentExplorer(DetourNavMeshTests.Plane(), 4f);
            int tested = 0;
            explorer.MarkSeen(Vector3.zero, Sight, point => { tested++; return point.x > 0f; }, 100);
            int inRadius = 0, marked = 0;
            for (int cell = 0; cell < explorer.CellCount; cell++)
            {
                if (!explorer.IsWalkable(cell) || DistanceXZ(explorer.GetCellPoint(cell), Vector3.zero) > Sight) continue;
                inRadius++;
                if (explorer.IsSeen(cell))
                {
                    marked++;
                    Assert.That(explorer.GetCellPoint(cell).x, Is.GreaterThan(0f));
                }
            }
            Assert.That(tested, Is.EqualTo(inRadius));
            Assert.That(marked, Is.GreaterThan(0).And.LessThan(inRadius));

            tested = 0;
            explorer.MarkSeen(Vector3.zero, Sight, point => { tested++; return true; }, 2);
            Assert.That(tested, Is.EqualTo(2), "the budget caps the tests");
            Assert.That(explorer.SeenCellCount, Is.EqualTo(marked + 2));
            tested = 0;
            explorer.MarkSeen(Vector3.zero, Sight, point => { tested++; return true; }, 100);
            Assert.That(tested, Is.EqualTo(inRadius - marked - 2), "seen cells are not tested again");
        }

        [Test]
        public void LimitedExplorationMarksOnlyCellsInView()
        {
            var settings = new LocalAgentSettings { perceptionMode = PerceptionMode.Limited, perceptionHz = 0f };
            var controller = new LocalAgentController { NavMesh = DetourNavMeshTests.Plane() };
            var perception = new LocalAgentPerception();
            perception.Update(settings, 0f, new[] { Actor(1, 0) }, 1, 1, Vector3.right);
            controller.Step(settings, perception, true, default, 0f);
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Exploring));
            for (int cell = 0; cell < controller.Explorer.CellCount; cell++)
            {
                Vector3 point = controller.Explorer.GetCellPoint(cell);
                if (controller.Explorer.IsSeen(cell) && DistanceXZ(point, Vector3.zero) > settings.closeAwarenessRadius)
                    Assert.That(Vector2.Angle(Vector2.right, new Vector2(point.x, point.z)), Is.LessThanOrEqualTo(55f));
            }
            Assert.That(controller.Explorer.SeenCellCount, Is.GreaterThan(0));
        }

        [Test]
        public void InvestigatingWalksTheNavMeshToWhereTheEnemyWasThenLooksAround()
        {
            var settings = new LocalAgentSettings { perceptionMode = PerceptionMode.Limited, perceptionHz = 0f,
                reactionSeconds = 0f, enemyTemplateIds = new uint[] { 100 }, investigateScanSeconds = 1f,
                aimErrorStartDegrees = 0f, aimErrorSettledDegrees = 0f };
            var controller = new LocalAgentController { NavMesh = DetourNavMeshTests.Plane() };
            var perception = new LocalAgentPerception();
            var weapon = new KernelLocalWeaponState { ammo = 10, flags = KernelConstants.LocalWeaponStateFlagWeaponIdValid };
            Vector3 position = Vector3.zero;
            float time = 0f;
            LocalAgentCommand Tick(params RenderEntityState[] others)
            {
                var self = Actor(1, 0); self.position = new KernelVec3(position.x, position.y, position.z);
                var states = new List<RenderEntityState> { self };
                states.AddRange(others);
                perception.Update(settings, time, states.ToArray(), states.Count, 1, controller.LastAimDirection);
                LocalAgentCommand command = controller.Step(settings, perception, true, weapon, 0f);
                position = Walk(controller.NavMesh, position, command.Move);
                time += 1f / 30f;
                return command;
            }

            // Seen straight ahead (+z) at 8 m, then it drops out of the sync.
            var enemy = Actor(3, 0, KernelActorType.Agent); enemy.position = new KernelVec3(0, 0, 8);
            Assert.That(Tick(enemy).Fire, Is.True);
            LocalAgentCommand chase = Tick();
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Investigating));
            Assert.That(controller.InvestigateTargetId, Is.EqualTo(3));
            Assert.That(chase.Move.y, Is.GreaterThan(0.9f));
            Assert.That(chase.AimDirection, Is.EqualTo(Vector3.forward));

            int steps = 0;
            while (steps++ < 200 && Tick().Move != Vector2.zero) { }
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Investigating));
            Assert.That(DistanceXZ(position, new Vector3(0, 0, 8)), Is.LessThanOrEqualTo(0.8f));

            // Arrived: it turns on the spot, all the way round, in a second.
            float turned = 0f;
            Vector3 aim = controller.LastAimDirection;
            for (int i = 0; i < 25; i++)
            {
                LocalAgentCommand look = Tick();
                Assert.That(look.Move, Is.EqualTo(Vector2.zero));
                Assert.That(controller.State, Is.EqualTo(LocalAgentState.Investigating));
                turned += Vector3.Angle(aim, look.AimDirection);
                aim = look.AimDirection;
            }
            Assert.That(turned, Is.GreaterThan(270f));
            for (int i = 0; i < 10; i++) Tick();
            Assert.That(controller.State, Is.EqualTo(LocalAgentState.Exploring), "done looking, back to exploring");
            Assert.That(controller.InvestigateTargetId, Is.Zero);
            Assert.That(perception.Actors.Count, Is.EqualTo(1), "still remembered, not searched again");
        }
    }
}
