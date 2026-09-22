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
    }
}
