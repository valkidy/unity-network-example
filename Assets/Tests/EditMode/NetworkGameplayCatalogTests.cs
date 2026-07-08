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
        public void DefaultBundlePath_PointsAtKernelPluginResource()
        {
            Assert.That(
                NetworkGameplayCatalogBundle.DefaultBundleDisplayPath,
                Is.EqualTo("Network Example Kernel/Runtime/Resources/gameplay_catalog_bundle/bundle.bytes"));
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
