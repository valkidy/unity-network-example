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
            params RenderEntityState[] states) =>
            controller.Step(settings, states, states.Length, 1, true,
                new KernelLocalWeaponState { weapon_id = 0, ammo = 10,
                    flags = KernelConstants.LocalWeaponStateFlagWeaponIdValid }, 0f);

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
    }
}
