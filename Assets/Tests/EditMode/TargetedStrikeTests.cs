using System.Collections.Generic;
using NetworkExample.Kernel;
using NetworkExample.UnityDemo.CameraSystem;
using NetworkExample.UnityDemo.Common;
using NetworkExample.UnityDemo.Rendering;
using NetworkExample.UnityDemo.UI;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class TargetedStrikeTests
    {
        private static readonly Vector3 FireOrigin = new Vector3(0f, 1f, 0f);
        private readonly List<GameObject> created = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject item in created)
            {
                if (item != null)
                {
                    Object.DestroyImmediate(item);
                }
            }

            created.Clear();
        }

        // --- landing point -------------------------------------------------

        [Test]
        public void Resolve_AimAtGroundInRange_LandsWhereTheAimMeetsTheGround()
        {
            TargetedStrikeTargeting.WorldRaycast world = FlatGround(0f);
            Vector3 aim = (new Vector3(0f, 0f, 10f) - FireOrigin).normalized;

            bool found = TargetedStrikeTargeting.TryResolve(
                FireOrigin, aim, 35f, world, null, out Vector3 landing);

            Assert.That(found, Is.True);
            Assert.That(Vector3.Distance(landing, new Vector3(0f, 0f, 10f)), Is.LessThan(0.01f));
        }

        [Test]
        public void Resolve_AimAtTheSky_IsRefused()
        {
            bool found = TargetedStrikeTargeting.TryResolve(
                FireOrigin, Vector3.up, 35f, FlatGround(0f), null, out _);

            Assert.That(found, Is.False);
        }

        [Test]
        public void Resolve_GroundBeyondMaxRange_IsRefused()
        {
            Vector3 aim = (new Vector3(0f, 0f, 50f) - FireOrigin).normalized;

            bool found = TargetedStrikeTargeting.TryResolve(
                FireOrigin, aim, 35f, FlatGround(0f), null, out _);

            Assert.That(found, Is.False);
        }

        [Test]
        public void Resolve_AimAtAWall_LandsAtItsFootOnTheShootersSide()
        {
            // A wall across +Z at z = 10, standing on the ground at y = 0.
            TargetedStrikeTargeting.WorldRaycast world =
                (Vector3 origin, Vector3 direction, float max,
                    out Vector3 point, out Vector3 normal, out float distance) =>
                {
                    if (direction.z > 0.0001f)
                    {
                        float t = (10f - origin.z) / direction.z;
                        if (t >= 0f && t <= max)
                        {
                            point = origin + direction * t;
                            normal = Vector3.back;
                            distance = t;
                            return true;
                        }
                    }

                    return GroundHit(0f, origin, direction, max, out point, out normal, out distance);
                };

            bool found = TargetedStrikeTargeting.TryResolve(
                FireOrigin, Vector3.forward, 35f, world, null, out Vector3 landing);

            Assert.That(found, Is.True);
            Assert.That(landing.y, Is.EqualTo(0f).Within(0.001f));
            Assert.That(landing.z, Is.EqualTo(10f - TargetedStrikeTargeting.WallBackOff).Within(0.001f));
        }

        [Test]
        public void Resolve_AimAtAnActor_LandsAtItsFeet()
        {
            var actors = new[]
            {
                new TargetedStrikeTargeting.ActorBox(
                    new Vector3(0f, 0.9f, 12f),
                    new Vector3(0.35f, 0.9f, 0.35f)),
            };

            bool found = TargetedStrikeTargeting.TryResolve(
                FireOrigin, Vector3.forward, 35f, FlatGround(0f), actors, out Vector3 landing);

            Assert.That(found, Is.True);
            Assert.That(landing.y, Is.EqualTo(0f).Within(0.001f));
            Assert.That(landing.z, Is.EqualTo(12f - 0.35f).Within(0.001f));
        }

        [Test]
        public void Resolve_WithNothingBelowTheHit_IsRefused()
        {
            // Only a wall: the drop has no ground to land on.
            TargetedStrikeTargeting.WorldRaycast world =
                (Vector3 origin, Vector3 direction, float max,
                    out Vector3 point, out Vector3 normal, out float distance) =>
                {
                    point = default;
                    normal = default;
                    distance = 0f;
                    if (direction.z <= 0.0001f)
                    {
                        return false;
                    }

                    distance = (10f - origin.z) / direction.z;
                    point = origin + direction * distance;
                    normal = Vector3.back;
                    return distance >= 0f && distance <= max;
                };

            bool found = TargetedStrikeTargeting.TryResolve(
                FireOrigin, Vector3.forward, 35f, world, null, out _);

            Assert.That(found, Is.False);
        }

        // --- aim convergence ------------------------------------------------

        [Test]
        public void ConvergedAim_FromAboveTheFirePoint_PointsAtWhatTheReticleIsOn()
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.localScale = new Vector3(20f, 1f, 20f);
            created.Add(ground);
            Physics.SyncTransforms();

            // Camera 1 m above and 3 m behind the fire point, 10 degrees down.
            Vector3 cameraPosition = FireOrigin + new Vector3(0f, 1f, -3f);
            Vector3 cameraForward = Quaternion.Euler(10f, 0f, 0f) * Vector3.forward;
            var ray = new Ray(cameraPosition, cameraForward);

            Vector3 target = ReticleAimConvergence.TargetPoint(ray, FireOrigin, ~0);
            Vector3 aim = ReticleAimConvergence.DirectionTo(FireOrigin, target, cameraForward);

            Assert.That(target.y, Is.EqualTo(0f).Within(0.01f));
            Assert.That(
                Physics.Raycast(FireOrigin, aim, out RaycastHit hit, 100f),
                Is.True);
            Assert.That(Vector3.Distance(hit.point, target), Is.LessThan(0.05f));

            // Sending the camera's own direction lands well short of the reticle.
            Assert.That(Physics.Raycast(FireOrigin, cameraForward, out RaycastHit parallel, 100f), Is.True);
            Assert.That(Vector3.Distance(parallel.point, target), Is.GreaterThan(2f));
        }

        [Test]
        public void ConvergedAim_WithNothingUnderTheReticle_UsesAFarPointAlongTheRay()
        {
            var ray = new Ray(new Vector3(0f, 1000f, 0f), Vector3.up);

            Vector3 target = ReticleAimConvergence.TargetPoint(ray, FireOrigin, 0, 500f);

            Assert.That(target, Is.EqualTo(new Vector3(0f, 1500f, 0f)));
        }

        // --- view lifecycle -------------------------------------------------

        [Test]
        public void ServerBackedProjectile_IsRemovedOnceItLeavesTheRenderState()
        {
            Applier(out NetworkEntityRegistry registry, out _, out _);
            NetworkRenderStateApplier applier = created[created.Count - 1]
                .GetComponent<NetworkRenderStateApplier>();

            RenderEntityState marker = Projectile(entityId: 7, netId: 900, templateId: 18);
            applier.Apply(new[] { marker }, 1);
            Assert.That(registry.TryGet(7, out _), Is.True);

            applier.Apply(System.Array.Empty<RenderEntityState>(), 0);

            Assert.That(registry.TryGet(7, out _), Is.False);
            Assert.That(registry.TryGetByNetId(900, out _), Is.False);
        }

        [Test]
        public void DerivedProjectile_IsRemovedOnceItLeavesTheRenderState()
        {
            Applier(out NetworkEntityRegistry registry, out _, out Transform root);
            NetworkRenderStateApplier applier = created[created.Count - 1]
                .GetComponent<NetworkRenderStateApplier>();

            RenderEntityState fuse = Projectile(entityId: 11, netId: 0, templateId: 22);
            RenderEntityState meteor = Projectile(entityId: 12, netId: 0, templateId: 19);
            applier.Apply(new[] { fuse, meteor }, 2);
            Assert.That(root.childCount, Is.EqualTo(2));

            applier.Apply(new[] { meteor }, 1);
            Assert.That(registry.TryGet(11, out _), Is.False);
            Assert.That(registry.TryGet(12, out _), Is.True);

            applier.Apply(System.Array.Empty<RenderEntityState>(), 0);
            Assert.That(root.childCount, Is.Zero);
        }

        [Test]
        public void LateDespawnOfARemovedMarker_DoesNothing()
        {
            Applier(out NetworkEntityRegistry registry, out _, out _);
            NetworkRenderStateApplier applier = created[created.Count - 1]
                .GetComponent<NetworkRenderStateApplier>();
            applier.Apply(new[] { Projectile(entityId: 7, netId: 900, templateId: 21) }, 1);
            applier.Apply(System.Array.Empty<RenderEntityState>(), 0);

            Assert.DoesNotThrow(() => applier.ApplyEntityLifecycleEvents(
                new[]
                {
                    new KernelEntityLifecycleEvent
                    {
                        type = KernelEntityLifecycleEventType.Destroyed,
                        net_id = 900,
                        entity_type = KernelEntityType.Projectile,
                    },
                },
                1));
            Assert.That(registry.TryGet(7, out _), Is.False);
        }

        // --- refusal --------------------------------------------------------

        [Test]
        public void EffectFailedResult_IsReportedOnce()
        {
            Applier(out _, out _, out _);
            NetworkRenderStateApplier applier = created[created.Count - 1]
                .GetComponent<NetworkRenderStateApplier>();
            int failures = 0;
            applier.LocalActionEffectFailed += _ => failures++;

            applier.ApplyLocalActionResults(
                1,
                new[]
                {
                    new KernelLocalActionResult
                    {
                        action_instance_id = 5,
                        result = KernelLocalActionResultType.Corrected,
                        reason = KernelLocalActionResultReason.EffectFailed,
                    },
                    new KernelLocalActionResult
                    {
                        action_instance_id = 6,
                        result = KernelLocalActionResultType.Accepted,
                    },
                },
                2);

            Assert.That(failures, Is.EqualTo(1));
        }

        [Test]
        public void Reticle_ShowsTargetStateAndFlashesOnRefusal()
        {
            var host = new GameObject("Reticle");
            created.Add(host);
            AimReticleView reticle = host.AddComponent<AimReticleView>();

            reticle.UpdateReticle(1f, new Vector2(0.5f, 0.5f));
            Color normal = reticle.CurrentColor;
            float normalGap = reticle.CurrentGap;

            reticle.SetTargetState(AimReticleView.TargetState.Invalid);
            reticle.UpdateReticle(1f, new Vector2(0.5f, 0.5f));
            Color invalid = reticle.CurrentColor;
            Assert.That(invalid, Is.Not.EqualTo(normal));

            reticle.SetTargetState(AimReticleView.TargetState.Valid);
            reticle.UpdateReticle(1f, new Vector2(0.5f, 0.5f));
            Assert.That(reticle.CurrentColor, Is.Not.EqualTo(invalid));

            reticle.FlashRejected();
            reticle.UpdateReticle(1f, new Vector2(0.5f, 0.5f));
            Assert.That(reticle.IsFlashingRejected, Is.True);
            Assert.That(reticle.CurrentColor, Is.EqualTo(invalid));
            Assert.That(reticle.CurrentGap, Is.GreaterThan(normalGap));
        }

        // --- preview --------------------------------------------------------

        [Test]
        public void Preview_OnlyShowsForTargetedStrikeWeapons()
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.localScale = new Vector3(20f, 1f, 20f);
            created.Add(ground);
            Physics.SyncTransforms();

            var host = new GameObject("Preview");
            created.Add(host);
            AimReticleView reticle = host.AddComponent<AimReticleView>();
            TargetedStrikePreview preview = host.AddComponent<TargetedStrikePreview>();
            preview.Configure(reticle);
            preview.ConfigureWeapons(new Dictionary<byte, float> { { 13, 35f } });
            Vector3 aim = (new Vector3(0f, 0f, 10f) - FireOrigin).normalized;

            preview.UpdatePreview(true, 3, FireOrigin, aim, null, 0, 1);
            Assert.That(preview.IsActive, Is.False);
            Assert.That(reticle.CurrentTargetState, Is.EqualTo(AimReticleView.TargetState.None));

            preview.UpdatePreview(true, 13, FireOrigin, aim, null, 0, 1);
            Assert.That(preview.HasTarget, Is.True);
            Assert.That(preview.Marker.activeSelf, Is.True);
            Assert.That(preview.Marker.GetComponentInChildren<Collider>(), Is.Null);
            Assert.That(reticle.CurrentTargetState, Is.EqualTo(AimReticleView.TargetState.Valid));

            preview.UpdatePreview(true, 13, FireOrigin, Vector3.up, null, 0, 1);
            Assert.That(preview.HasTarget, Is.False);
            Assert.That(preview.Marker.activeSelf, Is.False);
            Assert.That(reticle.CurrentTargetState, Is.EqualTo(AimReticleView.TargetState.Invalid));
        }

        // --- catalog and art -----------------------------------------------

        [Test]
        public void DefaultBundle_DeclaresTheThreeStrikeWeaponsWithTheirRange()
        {
            Assert.That(
                NetworkGameplayCatalogBundle.TryLoadDefault(out byte[] bundle, out string entry),
                Is.True);

            bool read = NetworkGameplayCatalogBundle.TryReadTargetedStrikeRanges(
                bundle, entry, out Dictionary<byte, float> ranges, out string diagnostic);

            Assert.That(read, Is.True, diagnostic);
            Assert.That(ranges.Count, Is.EqualTo(3));
            Assert.That(ranges[13], Is.EqualTo(35f));
            Assert.That(ranges[14], Is.EqualTo(35f));
            Assert.That(ranges[15], Is.EqualTo(40f));
        }

        [Test]
        public void DefaultPrefabCatalog_BindsEveryStrikeTemplate()
        {
            NetworkPrefabCatalog catalog = Resources.Load<NetworkPrefabCatalog>(
                NetworkPrefabRegistry.DefaultCatalogResourcePath);

            for (uint templateId = 18; templateId <= 25; ++templateId)
            {
                Assert.That(
                    catalog.TryGetProjectilePrefab(templateId, out GameObject prefab),
                    Is.True,
                    "template " + templateId);
                Assert.That(prefab.GetComponent<NetworkProjectileView>(), Is.Not.Null);
            }

            catalog.TryGetProjectilePrefab(24, out GameObject laser);
            Assert.That(laser.GetComponent<NetworkProjectileView>().SpanFromFirstPosition, Is.True);
            catalog.TryGetProjectilePrefab(19, out GameObject meteor);
            Assert.That(meteor.GetComponent<NetworkProjectileView>().FaceVelocity, Is.True);
        }

        [Test]
        public void LaserColumn_StretchesFromWhereItWasFirstSeen()
        {
            Applier(out NetworkEntityRegistry registry, out _, out _);
            NetworkRenderStateApplier applier = created[created.Count - 1]
                .GetComponent<NetworkRenderStateApplier>();

            RenderEntityState body = Projectile(entityId: 30, netId: 0, templateId: 24);
            body.position = new KernelVec3(5f, 60f, 5f);
            applier.Apply(new[] { body }, 1);
            body.position = new KernelVec3(5f, 20f, 5f);
            applier.Apply(new[] { body }, 1);

            Assert.That(registry.TryGet(30, out GameObject visual), Is.True);
            Transform column = visual.transform.GetChild(0);
            Renderer renderer = column.GetComponent<Renderer>();
            Assert.That(renderer.bounds.max.y, Is.EqualTo(60f).Within(0.05f));
            Assert.That(renderer.bounds.min.y, Is.EqualTo(20f).Within(0.05f));
        }

        [Test]
        public void MeteorBody_FacesItsVelocity()
        {
            Applier(out NetworkEntityRegistry registry, out _, out _);
            NetworkRenderStateApplier applier = created[created.Count - 1]
                .GetComponent<NetworkRenderStateApplier>();

            RenderEntityState body = Projectile(entityId: 31, netId: 0, templateId: 19);
            body.velocity = new KernelVec3(0f, -20f, 5f);
            applier.Apply(new[] { body }, 1);

            Assert.That(registry.TryGet(31, out GameObject visual), Is.True);
            Vector3 expected = new Vector3(0f, -20f, 5f).normalized;
            Assert.That(Vector3.Dot(visual.transform.forward, expected), Is.GreaterThan(0.999f));
        }

        // --- helpers --------------------------------------------------------

        private void Applier(
            out NetworkEntityRegistry registry,
            out NetworkPrefabRegistry prefabs,
            out Transform root)
        {
            var rootObject = new GameObject("TargetedStrikeTestsRoot");
            var host = new GameObject("TargetedStrikeTests");
            created.Add(rootObject);
            created.Add(host);
            registry = host.AddComponent<NetworkEntityRegistry>();
            prefabs = host.AddComponent<NetworkPrefabRegistry>();
            NetworkRenderStateApplier applier = host.AddComponent<NetworkRenderStateApplier>();
            applier.Configure(registry, prefabs, rootObject.transform);
            root = rootObject.transform;
        }

        private static RenderEntityState Projectile(ulong entityId, uint netId, uint templateId)
        {
            return new RenderEntityState
            {
                entity_id = entityId,
                net_id = netId,
                entity_type = KernelEntityType.Projectile,
                template_id = templateId,
                rotation = new KernelQuat(0f, 0f, 0f, 1f),
                status = RenderEntityStatus.Active,
            };
        }

        private static TargetedStrikeTargeting.WorldRaycast FlatGround(float height)
        {
            return (Vector3 origin, Vector3 direction, float max,
                out Vector3 point, out Vector3 normal, out float distance) =>
                GroundHit(height, origin, direction, max, out point, out normal, out distance);
        }

        private static bool GroundHit(
            float height,
            Vector3 origin,
            Vector3 direction,
            float max,
            out Vector3 point,
            out Vector3 normal,
            out float distance)
        {
            point = default;
            normal = Vector3.up;
            distance = 0f;
            if (direction.y >= -0.0001f)
            {
                return false;
            }

            distance = (height - origin.y) / direction.y;
            if (distance < 0f || distance > max)
            {
                return false;
            }

            point = origin + direction * distance;
            return true;
        }
    }
}
