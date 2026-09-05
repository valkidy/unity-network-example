using System.Collections.Generic;
using System.Security.Cryptography;
using NetworkExample.UnityDemo.Common;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class NetworkGameplayCatalogTests
    {
        [Test]
        public void TryLoadDefault_LoadsGameplayCatalogBundleBytesFromResources()
        {
            bool loaded = NetworkGameplayCatalogBundle.TryLoadDefault(
                out byte[] bundleBytes,
                out string entryPath);

            Assert.That(loaded, Is.True);
            Assert.That(entryPath, Is.EqualTo("gameplay_catalog.yaml"));
            Assert.That(bundleBytes, Is.Not.Null);
            Assert.That(bundleBytes, Has.Length.GreaterThan(2));
            Assert.That(bundleBytes[0], Is.EqualTo((byte)'P'));
            Assert.That(bundleBytes[1], Is.EqualTo((byte)'K'));
        }

        [Test]
        public void DefaultBundlePath_PointsAtKernelPluginResource()
        {
            Assert.That(
                NetworkGameplayCatalogBundle.DefaultBundleDisplayPath,
                Is.EqualTo("Network Example Kernel/Runtime/Resources/gameplay_catalog_bundle/bundle.bytes"));
        }

        [Test]
        public void TryReadPlayerWeaponLoadout_MapsSlotsToCatalogWeaponIds()
        {
            Assert.That(
                NetworkGameplayCatalogBundle.TryLoadDefault(
                    out byte[] bundleBytes,
                    out string entryPath),
                Is.True);

            bool loaded = NetworkGameplayCatalogBundle.TryReadPlayerWeaponLoadout(
                bundleBytes,
                entryPath,
                out byte[] weaponIds,
                out int activeWeaponSlot,
                out string diagnostic);

            Assert.That(loaded, Is.True, diagnostic);
            // Rocket, shotgun, grenade launcher, rifle -- number keys 1 to 4.
            Assert.That(weaponIds, Is.EqualTo(new byte[] { 3, 1, 7, 0 }));
            Assert.That(activeWeaponSlot, Is.EqualTo(0));
        }

        [Test]
        public void TryReadInstantWeaponPresentations_ReadsTheWeaponsThatSpawnNothing()
        {
            Assert.That(
                NetworkGameplayCatalogBundle.TryLoadDefault(
                    out byte[] bundleBytes,
                    out string entryPath),
                Is.True);

            bool loaded = NetworkGameplayCatalogBundle.TryReadInstantWeaponPresentations(
                bundleBytes,
                entryPath,
                out Dictionary<uint, NetworkInstantWeaponPresentation> weapons,
                out string diagnostic);

            Assert.That(loaded, Is.True, diagnostic);

            // rifle_fire. The rifle resolves by raycast, so nothing about its shot
            // reaches a client except this commit.
            Assert.That(weapons.ContainsKey(4096u), Is.True);
            NetworkInstantWeaponPresentation rifle = weapons[4096u];
            Assert.That(rifle.WeaponId, Is.EqualTo(0));
            Assert.That(rifle.ProjectileTemplateId, Is.EqualTo(10u));
            Assert.That(rifle.MaxRange, Is.EqualTo(100f));
            Assert.That(rifle.PelletCount, Is.EqualTo(1));

            // shotgun_fire, whose one commit is five rays.
            NetworkInstantWeaponPresentation shotgun = weapons[4097u];
            Assert.That(shotgun.WeaponId, Is.EqualTo(1));
            Assert.That(shotgun.ProjectileTemplateId, Is.EqualTo(11u));
            Assert.That(shotgun.MaxRange, Is.EqualTo(40f));
            Assert.That(shotgun.PelletCount, Is.EqualTo(5));
            Assert.That(shotgun.PelletSpread, Is.EqualTo(0.035f).Within(1e-6f));

            // rocket_fire. A projectile weapon spawns an entity the client draws
            // from its render state, and must not be drawn a second time.
            Assert.That(weapons.ContainsKey(4099u), Is.False);
        }

        [Test]
        public void PelletDirection_ReproducesTheFanTheKernelFires()
        {
            var shotgun = new NetworkInstantWeaponPresentation(1, 4097, 11, 40f, 5, 0.035f);
            Vector3 aim = Vector3.right;

            // The middle pellet of five takes no side offset, and an even pellet
            // is nudged up by half the spread -- weapon_system.cc builds the fan
            // against world axes, not against the aim's own frame.
            Vector3 expected = (aim + Vector3.up * 0.5f * 0.035f).normalized;

            Assert.That(
                Vector3.Angle(shotgun.PelletDirection(aim, 2), expected),
                Is.LessThan(0.01f));
            // And the outermost pellet leans a full two spreads to the side.
            Assert.That(
                Vector3.Angle(
                    shotgun.PelletDirection(aim, 4),
                    (aim + Vector3.forward * 2f * 0.035f + Vector3.up * 0.5f * 0.035f)
                        .normalized),
                Is.LessThan(0.01f));
        }

        [Test]
        public void TryLoadSynchronizedBundle_AcceptsMatchingPackagedBundle()
        {
            Assert.That(
                NetworkGameplayCatalogBundle.TryLoadDefault(
                    out byte[] expectedBytes,
                    out _),
                Is.True);
            byte[] digest;
            using (SHA256 sha256 = SHA256.Create())
            {
                digest = sha256.ComputeHash(expectedBytes);
            }
            var manifest = new NetworkExample.Kernel.KernelGameplayCatalogManifest
            {
                bundle_size = (uint)expectedBytes.Length,
                bundle_sha256 = digest,
            };

            bool loaded = NetworkGameplayCatalogBundle.TryLoadSynchronizedBundle(
                null,
                "127.0.0.1:7777",
                manifest,
                out byte[] actualBytes,
                out string diagnostic);

            Assert.That(loaded, Is.True, diagnostic);
            Assert.That(actualBytes, Is.EqualTo(expectedBytes));
        }

        [Test]
        public void FormatLoadResult_IncludesCatalogMetadata()
        {
            string message = NetworkGameplayCatalogBundle.FormatLoadResult(
                new NetworkExample.Kernel.KernelGameplayCatalogLoadResult
                {
                    status = NetworkExample.Kernel.KernelConstants.GameplayCatalogLoadStatusSuccess,
                    catalog_version = 3,
                    catalog_hash = 0x1234abcdUL,
                    projectile_template_count = 4,
                    collider_template_count = 5,
                    collider_binding_count = 6,
                });

            Assert.That(message, Does.Contain("version=3"));
            Assert.That(message, Does.Contain("hash=000000001234abcd"));
            Assert.That(message, Does.Contain("projectile_templates=4"));
            Assert.That(message, Does.Contain("collider_templates=5"));
            Assert.That(message, Does.Contain("collider_bindings=6"));
        }

        [Test]
        public void FormatLoadResult_IncludesDiagnosticForFailedCatalogLoad()
        {
            string message = NetworkGameplayCatalogBundle.FormatLoadResult(
                new NetworkExample.Kernel.KernelGameplayCatalogLoadResult
                {
                    status = NetworkExample.Kernel.KernelConstants.GameplayCatalogLoadStatusFailed,
                    error_code = NetworkExample.Kernel.KernelConstants.GameplayCatalogLoadErrorInvalidYaml,
                    source_kind = NetworkExample.Kernel.KernelConstants.GameplayCatalogLoadSourceBundle,
                    line = 7,
                    column = 11,
                    path = "gameplay_catalog.yaml",
                    field = "projectiles",
                    diagnostic = "invalid yaml",
                });

            Assert.That(message, Does.Contain("error=invalid yaml"));
            Assert.That(message, Does.Contain("error_code=2"));
            Assert.That(message, Does.Contain("source=2"));
            Assert.That(message, Does.Contain("path=gameplay_catalog.yaml"));
            Assert.That(message, Does.Contain("field=projectiles"));
            Assert.That(message, Does.Contain("line=7"));
            Assert.That(message, Does.Contain("column=11"));
        }
    }
}
