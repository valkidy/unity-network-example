using NetworkExample.Kernel;
using NetworkExample.UnityDemo.LocalAgent;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class LocalAgentPerceptionTests
    {
        private readonly LocalAgentSettings settings = new LocalAgentSettings();

        private static RenderEntityState Actor(uint id, float x, KernelActorType type = KernelActorType.Player) =>
            new RenderEntityState { net_id = id, entity_type = KernelEntityType.Actor,
                actor_type = type, template_id = 100, position = new KernelVec3(x, 2, 3) };

        [Test]
        public void OmniscientPerceivesEveryLivingActorExactlyAndSeparatesSelf()
        {
            var perception = new LocalAgentPerception();
            var states = new[] { Actor(2, 5, KernelActorType.Agent), Actor(1, 0), Actor(3, -4) };
            perception.Update(settings, 7f, states, states.Length, 1);

            Assert.That(perception.HasSelf, Is.True);
            Assert.That(perception.SelfPosition, Is.EqualTo(new Vector3(0, 2, 3)));
            Assert.That(perception.Actors.Count, Is.EqualTo(2));
            PerceivedActor enemy = perception.Actors[0];
            Assert.That(enemy.NetId, Is.EqualTo(2));
            Assert.That(enemy.ActorType, Is.EqualTo(KernelActorType.Agent));
            Assert.That(enemy.TemplateId, Is.EqualTo(100));
            Assert.That(enemy.LastKnownPosition, Is.EqualTo(new Vector3(5, 2, 3)));
            Assert.That(enemy.VisibleNow && enemy.Confirmed, Is.True);
            Assert.That(enemy.KnownDead, Is.False);
            Assert.That(enemy.LastSeenTime, Is.EqualTo(7f));
            Assert.That(enemy.LastKind, Is.EqualTo(StimulusKind.Seen));
            Assert.That(perception.Actors[1].NetId, Is.EqualTo(3));
        }

        [Test]
        public void DeadStaleInvalidAndNonActorRecordsAreNotPerceived()
        {
            var perception = new LocalAgentPerception();
            var stale = Actor(2, 1); stale.status = RenderEntityStatus.Stale;
            var dead = Actor(3, 1); dead.visual_flags = KernelConstants.VisualFlagDead;
            var prop = Actor(4, 1); prop.entity_type = KernelEntityType.Prop;
            var states = new[] { Actor(1, 0), stale, dead, prop, Actor(5, float.NaN), Actor(0, 1) };
            perception.Update(settings, 0f, states, states.Length, 1);
            Assert.That(perception.HasSelf, Is.True);
            Assert.That(perception.Actors.Count, Is.Zero);
        }

        [Test]
        public void SelfMustBeALivingPlayerWithinTheCount()
        {
            var perception = new LocalAgentPerception();
            var states = new[] { Actor(2, 5), Actor(1, 0) };
            perception.Update(settings, 0f, states, 1, 1);
            Assert.That(perception.HasSelf, Is.False);
            var notPlayer = new[] { Actor(1, 0, KernelActorType.Agent) };
            perception.Update(settings, 0f, notPlayer, 1, 1);
            Assert.That(perception.HasSelf, Is.False);
            Assert.That(perception.Actors.Count, Is.Zero, "its own id is never another actor");
            perception.Update(settings, 0f, states, 99, 1);
            Assert.That(perception.HasSelf, Is.True);
            perception.Reset();
            Assert.That(perception.HasSelf, Is.False);
            Assert.That(perception.Actors.Count, Is.Zero);
        }

        // --- Limited ---

        private sealed class FakeSight : ILineOfSight
        {
            public System.Func<Vector3, Vector3, bool> Clear = (eye, target) => true;
            public readonly System.Collections.Generic.List<(Vector3 eye, Vector3 target, uint a, uint b)> Calls =
                new System.Collections.Generic.List<(Vector3, Vector3, uint, uint)>();
            public bool IsClear(Vector3 eye, Vector3 target, uint ignoreNetIdA, uint ignoreNetIdB)
            {
                Calls.Add((eye, target, ignoreNetIdA, ignoreNetIdB));
                return Clear(eye, target);
            }
        }

        private static LocalAgentSettings Limited() => new LocalAgentSettings
        {
            perceptionMode = PerceptionMode.Limited, perceptionHz = 0f, reactionSeconds = 0f,
        };

        private static RenderEntityState At(uint id, float x, float z, KernelActorType type = KernelActorType.Agent) =>
            new RenderEntityState { net_id = id, entity_type = KernelEntityType.Actor,
                actor_type = type, template_id = 100, position = new KernelVec3(x, 0, z) };

        private static PerceivedActor? Find(LocalAgentPerception perception, uint id)
        {
            foreach (PerceivedActor actor in perception.Actors)
                if (actor.NetId == id) return actor;
            return null;
        }

        private static bool Sees(LocalAgentSettings limited, Vector3 facing, RenderEntityState target,
            ILineOfSight sight = null)
        {
            var perception = new LocalAgentPerception();
            perception.Update(limited, 0f, new[] { At(1, 0, 0, KernelActorType.Player), target }, 2, 1, facing, sight);
            PerceivedActor? actor = Find(perception, target.net_id);
            return actor.HasValue && actor.Value.VisibleNow;
        }

        [Test]
        public void LimitedSeesOnlyWithinTheFieldOfViewAndRange()
        {
            LocalAgentSettings limited = Limited();
            Vector3 north = Vector3.forward;
            Assert.That(Sees(limited, north, At(2, 0, 10)), Is.True);
            Assert.That(Sees(limited, north, At(2, 0, -10)), Is.False, "behind");
            float Rad(float degrees) => degrees * Mathf.Deg2Rad;
            Assert.That(Sees(limited, north, At(2, 10 * Mathf.Sin(Rad(54)), 10 * Mathf.Cos(Rad(54)))), Is.True);
            Assert.That(Sees(limited, north, At(2, 10 * Mathf.Sin(Rad(56)), 10 * Mathf.Cos(Rad(56)))), Is.False);
            Assert.That(Sees(limited, Vector3.right, At(2, 10, 0)), Is.True, "facing follows the aim");
            Assert.That(Sees(limited, new Vector3(0.1f, -1, 0), At(2, 10, 0)), Is.True, "only the horizontal part counts");
            Assert.That(Sees(limited, new Vector3(0.1f, -1, 0), At(2, 0, 10)), Is.False);
            // Range is measured from the eye, 1.6 m up.
            Assert.That(Sees(limited, north, At(2, 0, 39.9f)), Is.True);
            Assert.That(Sees(limited, north, At(2, 0, 40.1f)), Is.False);
        }

        [Test]
        public void CloseActorsAreNoticedWithoutLookingOrSight()
        {
            LocalAgentSettings limited = Limited();
            var wall = new FakeSight { Clear = (eye, target) => false };
            Assert.That(Sees(limited, Vector3.forward, At(2, 0, -2.4f), wall), Is.True);
            Assert.That(Sees(limited, Vector3.forward, At(2, 0, -2.6f), wall), Is.False);
            Assert.That(Sees(limited, Vector3.forward, At(2, 0, 5f), wall), Is.False, "in view but blocked");
        }

        [Test]
        public void EitherTheHeadOrTheFeetInSightIsEnough()
        {
            LocalAgentSettings limited = Limited();
            var sight = new FakeSight();
            sight.Clear = (eye, target) => target.y < 1f; // a waist-high gap: the head is hidden
            Assert.That(Sees(limited, Vector3.forward, At(2, 0, 10), sight), Is.True);
            Assert.That(sight.Calls.Count, Is.EqualTo(2), "head first, then feet");
            Assert.That(sight.Calls[0].eye, Is.EqualTo(new Vector3(0, 1.6f, 0)));
            Assert.That(sight.Calls[0].target, Is.EqualTo(new Vector3(0, 1.5f, 10)));
            Assert.That(sight.Calls[1].target, Is.EqualTo(new Vector3(0, 0.5f, 10)));
            Assert.That((sight.Calls[0].a, sight.Calls[0].b), Is.EqualTo((1u, 2u)), "observer and target are ignored");

            sight.Calls.Clear();
            sight.Clear = (eye, target) => target.y > 1f;
            Assert.That(Sees(limited, Vector3.forward, At(2, 0, 10), sight), Is.True);
            Assert.That(sight.Calls.Count, Is.EqualTo(1), "a clear head needs no second test");
            sight.Clear = (eye, target) => false;
            Assert.That(Sees(limited, Vector3.forward, At(2, 0, 10), sight), Is.False);
        }

        [Test]
        public void AnActorIsConfirmedAfterStayingInSightForTheReactionTime()
        {
            LocalAgentSettings limited = Limited();
            limited.reactionSeconds = 0.25f;
            var perception = new LocalAgentPerception();
            var sight = new FakeSight();
            RenderEntityState[] states = { At(1, 0, 0, KernelActorType.Player), At(2, 0, 10) };
            void Step(float time) => perception.Update(limited, time, states, 2, 1, Vector3.forward, sight);

            Step(0f);
            Assert.That(Find(perception, 2).Value.Confirmed, Is.False);
            Step(0.2f);
            Assert.That(Find(perception, 2).Value.Confirmed, Is.False);
            sight.Clear = (eye, target) => false;
            Step(0.3f);
            Assert.That(Find(perception, 2).Value.Confirmed, Is.False, "sight lost before confirming");
            sight.Clear = (eye, target) => true;
            Step(0.4f);
            Step(0.6f);
            Assert.That(Find(perception, 2).Value.Confirmed, Is.False, "the reaction time starts over");
            Step(0.65f);
            Assert.That(Find(perception, 2).Value.Confirmed, Is.True);
            sight.Clear = (eye, target) => false;
            Step(1f);
            Assert.That(Find(perception, 2).Value.VisibleNow, Is.False);
            Assert.That(Find(perception, 2).Value.Confirmed, Is.True, "stays confirmed while remembered");
        }

        [Test]
        public void MemoryKeepsTheLastKnownPositionUntilItIsForgotten()
        {
            LocalAgentSettings limited = Limited();
            var perception = new LocalAgentPerception();
            var self = At(1, 0, 0, KernelActorType.Player);
            perception.Update(limited, 0f, new[] { self, At(2, 0, 10) }, 2, 1, Vector3.forward);
            perception.Update(limited, 1f, new[] { self, At(2, 0, -10) }, 2, 1, Vector3.forward);
            PerceivedActor remembered = Find(perception, 2).Value;
            Assert.That(remembered.VisibleNow, Is.False);
            Assert.That(remembered.LastKnownPosition, Is.EqualTo(new Vector3(0, 0, 10)), "not where it went");
            Assert.That(remembered.LastSeenTime, Is.EqualTo(0f));
            Assert.That(remembered.TemplateId, Is.EqualTo(100));
            perception.Update(limited, 8f, new[] { self }, 1, 1, Vector3.forward);
            Assert.That(Find(perception, 2).HasValue, Is.True, "gone from the sync, still remembered");
            perception.Update(limited, 8.1f, new[] { self }, 1, 1, Vector3.forward);
            Assert.That(Find(perception, 2).HasValue, Is.False);
        }

        [Test]
        public void OnlyASeenDeathIsKnownAndStaleRecordsAreNeverSeen()
        {
            LocalAgentSettings limited = Limited();
            var perception = new LocalAgentPerception();
            var self = At(1, 0, 0, KernelActorType.Player);
            var ahead = At(2, 0, 10);
            var behind = At(3, 0, -10);
            perception.Update(limited, 0f, new[] { self, ahead, At(3, 0, 10) }, 3, 1, Vector3.forward);
            ahead.visual_flags = behind.visual_flags = KernelConstants.VisualFlagDead;
            perception.Update(limited, 1f, new[] { self, ahead, behind }, 3, 1, Vector3.forward);
            Assert.That(Find(perception, 2).Value.KnownDead, Is.True);
            Assert.That(Find(perception, 3).Value.KnownDead, Is.False, "died out of sight");

            var stale = At(4, 0, 10);
            stale.status = RenderEntityStatus.Stale;
            perception.Update(limited, 2f, new[] { self, stale }, 2, 1, Vector3.forward);
            Assert.That(Find(perception, 4).HasValue, Is.False);
        }

        [Test]
        public void SightIsTestedAtThePerceptionRateAndWhatIsInViewIsTrackedBetween()
        {
            LocalAgentSettings limited = Limited();
            limited.perceptionHz = 10f;
            var perception = new LocalAgentPerception();
            var sight = new FakeSight();
            var self = At(1, 0, 0, KernelActorType.Player);
            perception.Update(limited, 0f, new[] { self, At(2, 0, 10) }, 2, 1, Vector3.forward, sight);
            Assert.That(sight.Calls.Count, Is.EqualTo(1));
            perception.Update(limited, 0.05f, new[] { self, At(2, 1, 10), At(3, 0, 5) }, 3, 1, Vector3.forward, sight);
            Assert.That(sight.Calls.Count, Is.EqualTo(1), "no sight test between passes");
            Assert.That(Find(perception, 2).Value.LastKnownPosition, Is.EqualTo(new Vector3(1, 0, 10)), "tracked");
            Assert.That(Find(perception, 3).HasValue, Is.False, "not noticed until the next pass");
            perception.Update(limited, 0.1f, new[] { self, At(2, 1, 10), At(3, 0, 5) }, 3, 1, Vector3.forward, sight);
            Assert.That(sight.Calls.Count, Is.EqualTo(3));
            Assert.That(perception.LastSightTests, Is.EqualTo(2));
            Assert.That(Find(perception, 3).Value.VisibleNow, Is.True);
        }

        [Test]
        public void ChangingModeOrPlayerForgetsEverything()
        {
            LocalAgentSettings limited = Limited();
            var perception = new LocalAgentPerception();
            var states = new[] { At(1, 0, 0, KernelActorType.Player), At(2, 0, 10), At(5, 0, 20, KernelActorType.Player) };
            perception.Update(limited, 0f, states, 3, 1, Vector3.forward);
            perception.Update(limited, 1f, new[] { states[0] }, 1, 1, Vector3.forward);
            Assert.That(perception.Actors.Count, Is.EqualTo(2));
            perception.Update(limited, 2f, new[] { states[2] }, 1, 5, Vector3.back);
            Assert.That(perception.Actors.Count, Is.Zero, "a new local player starts with no memory");
            perception.Update(limited, 3f, states, 3, 1, Vector3.back);
            limited.perceptionMode = PerceptionMode.Omniscient;
            perception.Update(limited, 4f, states, 3, 1, Vector3.back);
            Assert.That(perception.Actors.Count, Is.EqualTo(2));
            limited.perceptionMode = PerceptionMode.Limited;
            perception.Update(limited, 5f, new[] { states[0] }, 1, 1, Vector3.back);
            Assert.That(perception.Actors.Count, Is.Zero);
        }
    }
}
