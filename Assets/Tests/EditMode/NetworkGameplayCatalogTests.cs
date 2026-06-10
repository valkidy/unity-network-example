using NetworkExample.UnityDemo.Common;
using NUnit.Framework;

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
        public void FormatLoadResult_IncludesCatalogMetadata()
        {
            string message = NetworkGameplayCatalogBundle.FormatLoadResult(
                new NetworkExample.Kernel.KernelGameplayCatalogLoadResult
                {
                    success = true,
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
    }
}
