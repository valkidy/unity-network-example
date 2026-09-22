using NetworkExample.Kernel;
using NetworkExample.UnityDemo.Common;
using NetworkExample.UnityDemo.LocalAgent;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class KernelColliderShapeSourceTests
    {
        private const uint ActorBox = 10, PropSphere = 11, RocketSphere = 12, RocketTemplate = 7;

        private static KernelColliderShapeSource Source(int capacity = 8)
        {
            var source = new KernelColliderShapeSource("test", capacity);
            source.SetCatalog(
                new[]
                {
                    new KernelColliderTemplateDefinition { template_id = ActorBox, shape_type = (byte)KernelColliderShapeType.OrientedBox,
                        center = new KernelVec3(1, 0, 0), shape_params = new KernelVec4(0.4f, 0.9f, 0.4f, 0),
                        purpose_flags = (uint)KernelColliderPurpose.Hit },
                    new KernelColliderTemplateDefinition { template_id = PropSphere, shape_type = (byte)KernelColliderShapeType.Sphere,
                        shape_params = new KernelVec4(0.5f, 0, 0, 0), purpose_flags = (uint)KernelColliderPurpose.Hit },
                    new KernelColliderTemplateDefinition { template_id = RocketSphere, shape_type = (byte)KernelColliderShapeType.Sphere,
                        shape_params = new KernelVec4(0.2f, 0, 0, 0), purpose_flags = (uint)KernelColliderPurpose.Damage },
                },
                new[]
                {
                    new KernelColliderBindingDefinition { entity_type = (ushort)KernelEntityType.Actor,
                        collider_template_id = ActorBox, local_position = new KernelVec3(0, 1, 0) },
                },
                new[]
                {
                    new KernelProjectileTemplateDefinition { projectile_template_id = RocketTemplate,
                        mechanics = new KernelProjectileMechanicsDefinition { collider_template_id = RocketSphere } },
                });
            return source;
        }

        private static RenderEntityState Entity(uint id, KernelEntityType type, Vector3 position, float yaw = 0f)
        {
            Quaternion rotation = Quaternion.Euler(0, yaw, 0);
            return new RenderEntityState { net_id = id, entity_type = type,
                position = new KernelVec3(position.x, position.y, position.z),
                rotation = new KernelQuat(rotation.x, rotation.y, rotation.z, rotation.w) };
        }

        private static Vector3 Center(KernelColliderShapeView shape) =>
            new Vector3(shape.world_center.x, shape.world_center.y, shape.world_center.z);

        [Test]
        public void BoundCollidersArePlacedAtTheRenderTransformPlusTheirOffset()
        {
            KernelColliderShapeSource source = Source();
            var states = new[] { Entity(4, KernelEntityType.Actor, new Vector3(5, 0, 0), yaw: 90) };
            source.CaptureFromCatalog(states, 1);

            Assert.That(source.Count, Is.EqualTo(1));
            Assert.That(source.LiveCount, Is.Zero);
            KernelColliderShapeView shape = source.Shapes[0];
            Assert.That(shape.entity_net_id, Is.EqualTo(4));
            Assert.That(shape.collider_template_id, Is.EqualTo(ActorBox));
            Assert.That(shape.shape_type, Is.EqualTo((byte)KernelColliderShapeType.OrientedBox));
            Assert.That(shape.shape_params.y, Is.EqualTo(0.9f));
            // Template centre (1,0,0) plus binding offset (0,1,0), turned 90 degrees: x goes to -z.
            Assert.That(Vector3.Distance(Center(shape), new Vector3(5, 1, -1)), Is.LessThan(1e-4f));
            var rotation = new Quaternion(shape.world_rotation.x, shape.world_rotation.y,
                shape.world_rotation.z, shape.world_rotation.w);
            Assert.That(Quaternion.Angle(rotation, Quaternion.Euler(0, 90, 0)), Is.LessThan(0.01f));
        }

        [Test]
        public void InstanceColliderWinsThenBindingThenProjectileTemplate()
        {
            KernelColliderShapeSource source = Source();
            var withInstance = Entity(1, KernelEntityType.Actor, Vector3.zero);
            withInstance.collider_template_id = PropSphere;
            var rocket = Entity(2, KernelEntityType.Projectile, Vector3.zero);
            rocket.template_id = RocketTemplate;
            var unbound = Entity(3, KernelEntityType.Prop, Vector3.zero);
            var missing = Entity(5, KernelEntityType.Prop, Vector3.zero);
            missing.collider_template_id = 999;
            var states = new[] { withInstance, rocket, unbound, missing };
            source.CaptureFromCatalog(states, states.Length);

            Assert.That(source.Count, Is.EqualTo(2));
            Assert.That(source.Shapes[0].collider_template_id, Is.EqualTo(PropSphere));
            // The binding's offset still applies to an instance collider on a bound entity type.
            Assert.That(Vector3.Distance(Center(source.Shapes[0]), new Vector3(0, 1, 0)), Is.LessThan(1e-4f));
            Assert.That(source.Shapes[1].collider_template_id, Is.EqualTo(RocketSphere));
            Assert.That(source.Shapes[1].entity_type, Is.EqualTo((ushort)KernelEntityType.Projectile));
            Assert.That(source.TryGetProjectileTemplate(RocketTemplate, out _), Is.True);
            Assert.That(source.TryGetColliderTemplate(ActorBox, out _), Is.True);
            Assert.That(source.ColliderTemplateCount, Is.EqualTo(3));
            Assert.That(source.ColliderBindingCount, Is.EqualTo(1));
        }

        [Test]
        public void BufferGrowsInsteadOfTruncating()
        {
            KernelColliderShapeSource source = Source(capacity: 1);
            var states = new[]
            {
                Entity(1, KernelEntityType.Actor, Vector3.zero), Entity(2, KernelEntityType.Actor, Vector3.one),
                Entity(3, KernelEntityType.Actor, Vector3.right),
            };
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"\[test\] Collider shape buffer grew from 1 to 2"));
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"\[test\] Collider shape buffer grew from 2 to 4"));
            source.CaptureFromCatalog(states, 99);
            Assert.That(source.Count, Is.EqualTo(3));
            Assert.That(source.Shapes[2].entity_net_id, Is.EqualTo(3));

            source.Clear();
            Assert.That(source.Count, Is.Zero);
            source.CaptureFromCatalog(null, 3);
            Assert.That(source.Count, Is.Zero);
            source.InvalidateCatalog();
            Assert.That(source.CatalogLoaded, Is.False);
            source.CaptureFromCatalog(states, 3);
            Assert.That(source.Count, Is.Zero, "nothing to rebuild from");
        }

        [Test]
        public void ARebuiltActorColliderBlocksSightButNotItsOwn()
        {
            KernelColliderShapeSource source = Source();
            var states = new[]
            {
                Entity(1, KernelEntityType.Actor, new Vector3(-5, 0, 0)),
                Entity(2, KernelEntityType.Actor, new Vector3(-1, 0, 0)), // box centred on (0,1,0)
                Entity(3, KernelEntityType.Actor, new Vector3(5, 0, 0)),
            };
            source.CaptureFromCatalog(states, states.Length);
            var sight = new ShapeLineOfSight();
            sight.SetShapes(source.Shapes, source.Count);
            Vector3 eye = new Vector3(-5, 1.6f, 0), target = new Vector3(5, 1f, 0);
            Assert.That(sight.IsClear(eye, target, 1, 3), Is.False);
            Assert.That(sight.IsClear(eye, target, 1, 2), Is.True);
            Assert.That(sight.IsClear(eye + Vector3.forward * 2, target + Vector3.forward * 2, 1, 3), Is.True, "beside it");
        }
    }
}
