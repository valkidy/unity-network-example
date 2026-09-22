using NetworkExample.Kernel;
using NetworkExample.UnityDemo.LocalAgent;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class LineOfSightGeometryTests
    {
        private static readonly Vector3 West = new Vector3(-5, 0, 0), East = new Vector3(5, 0, 0);

        [Test]
        public void SphereBlocksLinesThroughOrTouchingItButNotShortOfIt()
        {
            Assert.That(LineOfSightGeometry.SegmentHitsSphere(West, East, Vector3.zero, 1f), Is.True);
            Assert.That(LineOfSightGeometry.SegmentHitsSphere(West, East, new Vector3(0, 0, 1f), 1f), Is.True, "tangent");
            Assert.That(LineOfSightGeometry.SegmentHitsSphere(West, East, new Vector3(0, 0, 1.01f), 1f), Is.False);
            Assert.That(LineOfSightGeometry.SegmentHitsSphere(West, new Vector3(-2, 0, 0), Vector3.zero, 1f), Is.False, "stops short");
            Assert.That(LineOfSightGeometry.SegmentHitsSphere(Vector3.zero, East, Vector3.zero, 1f), Is.True, "starts inside");
            Assert.That(LineOfSightGeometry.SegmentHitsSphere(Vector3.zero, Vector3.zero, Vector3.zero, 1f), Is.True, "a point inside");
            Assert.That(LineOfSightGeometry.SegmentHitsSphere(West, East, Vector3.zero, 0f), Is.False);
            Assert.That(LineOfSightGeometry.SegmentHitsSphere(West, East, Vector3.zero, float.NaN), Is.False);
        }

        [Test]
        public void AabbBlocksLinesThroughItsFacesEdgesAndInside()
        {
            Vector3 half = Vector3.one;
            Assert.That(LineOfSightGeometry.SegmentHitsAabb(West, East, Vector3.zero, half), Is.True);
            Assert.That(LineOfSightGeometry.SegmentHitsAabb(West, East, new Vector3(0, 0, 1f), half), Is.True, "along a face");
            Assert.That(LineOfSightGeometry.SegmentHitsAabb(West, East, new Vector3(0, 0, 1.01f), half), Is.False);
            Assert.That(LineOfSightGeometry.SegmentHitsAabb(West, new Vector3(-1.01f, 0, 0), Vector3.zero, half), Is.False, "stops short");
            Assert.That(LineOfSightGeometry.SegmentHitsAabb(West, new Vector3(-1f, 0, 0), Vector3.zero, half), Is.True, "reaches the face");
            Assert.That(LineOfSightGeometry.SegmentHitsAabb(new Vector3(0.5f, 0.5f, 0), new Vector3(0.2f, 0, 0), Vector3.zero, half), Is.True, "inside");
            // Diagonal past a corner: x + z = 2.2 never enters |x| <= 1, |z| <= 1.
            Assert.That(LineOfSightGeometry.SegmentHitsAabb(new Vector3(-1, 0, 3.2f), new Vector3(3.2f, 0, -1), Vector3.zero, half), Is.False);
            Assert.That(LineOfSightGeometry.SegmentHitsAabb(new Vector3(-1, 0, 2.8f), new Vector3(2.8f, 0, -1), Vector3.zero, half), Is.True);
            Assert.That(LineOfSightGeometry.SegmentHitsAabb(West, East, Vector3.zero, new Vector3(1, 0, 1)), Is.False, "flat box");
        }

        [Test]
        public void OrientedBoxIsTestedInItsOwnFrame()
        {
            var half = new Vector3(2f, 1f, 0.2f);
            Vector3 a = new Vector3(-5, 0, 1.5f), b = new Vector3(5, 0, 1.5f);
            Assert.That(LineOfSightGeometry.SegmentHitsOrientedBox(a, b, Vector3.zero, half, Quaternion.identity), Is.False);
            Assert.That(LineOfSightGeometry.SegmentHitsOrientedBox(a, b, Vector3.zero, half, Quaternion.Euler(0, 90, 0)), Is.True);
            // A unit cube turned 45 degrees reaches sqrt(2) along x.
            Vector3 c = new Vector3(1.3f, 0, -5), d = new Vector3(1.3f, 0, 5);
            Assert.That(LineOfSightGeometry.SegmentHitsOrientedBox(c, d, Vector3.zero, Vector3.one, Quaternion.identity), Is.False);
            Assert.That(LineOfSightGeometry.SegmentHitsOrientedBox(c, d, Vector3.zero, Vector3.one, Quaternion.Euler(0, 45, 0)), Is.True);
            Assert.That(LineOfSightGeometry.SegmentHitsOrientedBox(c + Vector3.right * 0.2f, d + Vector3.right * 0.2f,
                Vector3.zero, Vector3.one, Quaternion.Euler(0, 45, 0)), Is.False);
            var unnormalised = new Quaternion(0f, 2f * Mathf.Sin(Mathf.PI / 8), 0f, 2f * Mathf.Cos(Mathf.PI / 8));
            Assert.That(LineOfSightGeometry.SegmentHitsOrientedBox(c, d, Vector3.zero, Vector3.one, unnormalised), Is.True);
        }

        [Test]
        public void CapsuleBlocksWithinItsRadius()
        {
            Vector3 start = new Vector3(0, 0, -3), end = new Vector3(0, 0, 3);
            Assert.That(LineOfSightGeometry.SegmentHitsCapsule(West, East, start, end, 0.5f), Is.True, "crossing");
            Assert.That(LineOfSightGeometry.SegmentHitsCapsule(new Vector3(-5, 0.4f, 0), new Vector3(5, 0.4f, 0), start, end, 0.5f), Is.True);
            Assert.That(LineOfSightGeometry.SegmentHitsCapsule(new Vector3(-5, 0.6f, 0), new Vector3(5, 0.6f, 0), start, end, 0.5f), Is.False);
            Assert.That(LineOfSightGeometry.SegmentHitsCapsule(new Vector3(-5, 0, 3.4f), new Vector3(5, 0, 3.4f), start, end, 0.5f), Is.True, "round cap");
            Assert.That(LineOfSightGeometry.SegmentHitsCapsule(new Vector3(-5, 0, 3.6f), new Vector3(5, 0, 3.6f), start, end, 0.5f), Is.False);
            Assert.That(LineOfSightGeometry.SegmentHitsCapsule(West, East, start, end, 0f), Is.False, "a tracer line is not solid");
        }

        [Test]
        public void SegmentDistanceHandlesParallelSkewAndDegenerateSegments()
        {
            Assert.That(LineOfSightGeometry.SegmentDistanceSq(West, East, new Vector3(-5, 2, 0), new Vector3(5, 2, 0)), Is.EqualTo(4f).Within(1e-5f));
            Assert.That(LineOfSightGeometry.SegmentDistanceSq(West, East, new Vector3(0, 1, -1), new Vector3(0, 1, 1)), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(LineOfSightGeometry.SegmentDistanceSq(West, East, new Vector3(7, 0, 0), new Vector3(9, 0, 0)), Is.EqualTo(4f).Within(1e-5f), "collinear, apart");
            Assert.That(LineOfSightGeometry.SegmentDistanceSq(West, East, new Vector3(0, 3, 0), new Vector3(0, 3, 0)), Is.EqualTo(9f).Within(1e-5f), "point");
            Assert.That(LineOfSightGeometry.SegmentDistanceSq(Vector3.zero, Vector3.zero, Vector3.up, Vector3.up), Is.EqualTo(1f).Within(1e-5f));
        }

        private static KernelColliderShapeView Shape(KernelColliderShapeType type, Vector3 center,
            Vector4 parameters, uint netId = 9, uint purpose = (uint)KernelColliderPurpose.Hit,
            KernelEntityType entityType = KernelEntityType.Prop) =>
            new KernelColliderShapeView
            {
                entity_net_id = netId, entity_type = (ushort)entityType, shape_type = (byte)type,
                purpose_flags = purpose, world_center = new KernelVec3(center.x, center.y, center.z),
                shape_params = new KernelVec4(parameters.x, parameters.y, parameters.z, parameters.w),
            };

        [Test]
        public void ShapeViewsAreReadByTheirType()
        {
            Assert.That(LineOfSightGeometry.SegmentHitsShape(West, East,
                Shape(KernelColliderShapeType.Sphere, Vector3.zero, new Vector4(1, 0, 0, 0))), Is.True);
            Assert.That(LineOfSightGeometry.SegmentHitsShape(West, East,
                Shape(KernelColliderShapeType.Aabb, new Vector3(0, 0, 1.5f), new Vector4(1, 1, 1, 0))), Is.False);
            Assert.That(LineOfSightGeometry.SegmentHitsShape(West, East,
                Shape(KernelColliderShapeType.Aabb, new Vector3(0, 0, 0.5f), new Vector4(1, 1, 1, 0))), Is.True);

            // An unset (all-zero) rotation reads as identity.
            var box = Shape(KernelColliderShapeType.OrientedBox, Vector3.zero, new Vector4(2, 1, 0.2f, 0));
            Vector3 a = new Vector3(-5, 0, 1.5f), b = new Vector3(5, 0, 1.5f);
            Assert.That(LineOfSightGeometry.SegmentHitsShape(a, b, box), Is.False);
            Quaternion turn = Quaternion.Euler(0, 90, 0);
            box.world_rotation = new KernelQuat(turn.x, turn.y, turn.z, turn.w);
            Assert.That(LineOfSightGeometry.SegmentHitsShape(a, b, box), Is.True);

            // Segment: explicit endpoints; x is the length, y the radius.
            var capsule = Shape(KernelColliderShapeType.Segment, Vector3.zero, new Vector4(100, 0.5f, 0, 0));
            capsule.segment_start = new KernelVec3(0, 0, -3);
            capsule.segment_end = new KernelVec3(0, 0, 3);
            Assert.That(LineOfSightGeometry.SegmentHitsShape(new Vector3(-5, 0, 3.4f), new Vector3(5, 0, 3.4f), capsule), Is.True);
            Assert.That(LineOfSightGeometry.SegmentHitsShape(new Vector3(-5, 2, 0), new Vector3(5, 2, 0), capsule), Is.False,
                "the length is not read as the radius");

            Assert.That(LineOfSightGeometry.SegmentHitsShape(West, East,
                Shape(KernelColliderShapeType.Cone, Vector3.zero, new Vector4(10, 90, 0, 0))), Is.False, "cones are not solid");
            Assert.That(LineOfSightGeometry.SegmentHitsShape(West, East,
                Shape(KernelColliderShapeType.Sphere, new Vector3(float.NaN, 0, 0), new Vector4(1, 0, 0, 0))), Is.False);
            Assert.That(LineOfSightGeometry.SegmentHitsShape(West, East,
                Shape(KernelColliderShapeType.Aabb, Vector3.zero, new Vector4(float.PositiveInfinity, 1, 1, 0))), Is.False);
        }

        [Test]
        public void LineOfSightIgnoresTheObserverTheTargetAndNonSolidShapes()
        {
            var sight = new ShapeLineOfSight();
            Vector4 unit = new Vector4(1, 1, 1, 0);
            var shapes = new[]
            {
                Shape(KernelColliderShapeType.Aabb, West, unit, netId: 1),                              // observer
                Shape(KernelColliderShapeType.Aabb, East, unit, netId: 2),                              // target
                Shape(KernelColliderShapeType.Sphere, Vector3.zero, unit, purpose: (uint)KernelColliderPurpose.Vision),
                Shape(KernelColliderShapeType.Sphere, Vector3.zero, unit, purpose: (uint)KernelColliderPurpose.Trigger),
                Shape(KernelColliderShapeType.Sphere, Vector3.zero, unit, entityType: KernelEntityType.Projectile),
                Shape(KernelColliderShapeType.Sphere, new Vector3(0, 0, 3), unit),                      // off the line
            };
            sight.SetShapes(shapes, shapes.Length);
            Assert.That(sight.IsClear(West, East, 1, 2), Is.True);
            Assert.That(sight.IsClear(West, East, 1, 3), Is.False, "someone else's collider blocks");

            shapes[5].world_center = new KernelVec3(0, 0, 0);
            shapes[5].purpose_flags = (uint)(KernelColliderPurpose.Hit | KernelColliderPurpose.Damage);
            Assert.That(sight.IsClear(West, East, 1, 2), Is.False, "an actor or prop in between");
            sight.SetShapes(shapes, 5);
            Assert.That(sight.IsClear(West, East, 1, 2), Is.True, "only the first count shapes are read");

            // A shape without an entity is never mistaken for the observer or target.
            shapes[0].entity_net_id = 0;
            Assert.That(sight.IsClear(West, East, 0, 2), Is.False);
            sight.SetShapes(null, 3);
            Assert.That(sight.ShapeCount, Is.Zero);
            Assert.That(sight.IsClear(West, East, 0, 0), Is.True);
        }

        [Test]
        public void TerrainIsAskedOnlyWhenNoShapeBlocks()
        {
            int calls = 0;
            bool terrainBlocks = false;
            var sight = new ShapeLineOfSight { TerrainBlocks = (from, to) => { calls++; return terrainBlocks; } };
            Assert.That(sight.IsClear(West, East, 0, 0), Is.True);
            terrainBlocks = true;
            Assert.That(sight.IsClear(West, East, 0, 0), Is.False);
            Assert.That(calls, Is.EqualTo(2));
            sight.SetShapes(new[] { Shape(KernelColliderShapeType.Sphere, Vector3.zero, new Vector4(1, 0, 0, 0)) }, 1);
            Assert.That(sight.IsClear(West, East, 0, 0), Is.False);
            Assert.That(calls, Is.EqualTo(2), "a blocking shape short-circuits the physics query");
        }
    }
}
