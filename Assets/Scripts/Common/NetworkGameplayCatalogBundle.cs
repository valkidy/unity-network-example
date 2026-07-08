using NetworkExample.Kernel;
using UnityEngine;
#if UNITY_EDITOR
using System.IO;
using UnityEditor.PackageManager;
#endif

namespace NetworkExample.UnityDemo.Common
{
    public static class NetworkGameplayCatalogBundle
    {
        public const string DefaultBundleDisplayPath =
            "Network Example Kernel/Runtime/Resources/gameplay_catalog_bundle/bundle.bytes";
        public const string DefaultResourcePath = "gameplay_catalog_bundle/bundle";
        public const string DefaultEntryPath = "gameplay_catalog.yaml";

        public static bool TryLoadDefault(out byte[] bundleBytes, out string entryPath)
        {
            entryPath = DefaultEntryPath;

            if (TryLoadKernelPackageBundle(out bundleBytes))
            {
                return true;
            }

            TextAsset bundleAsset = Resources.Load<TextAsset>(DefaultResourcePath);
            if (bundleAsset == null)
            {
                bundleBytes = null;
                Debug.LogError(
                    "Gameplay catalog bundle not found at " +
                    DefaultBundleDisplayPath +
                    " or Resources/" +
                    DefaultResourcePath +
                    ".bytes.");
                return false;
            }

            bundleBytes = bundleAsset.bytes;
            if (bundleBytes == null || bundleBytes.Length == 0)
            {
                Debug.LogError(
                    "Gameplay catalog bundle at Resources/" +
                    DefaultResourcePath +
                    ".bytes is empty.");
                return false;
            }

            return true;
        }

        private static bool TryLoadKernelPackageBundle(out byte[] bundleBytes)
        {
#if UNITY_EDITOR
            PackageInfo packageInfo = PackageInfo.FindForAssembly(
                typeof(global::NetworkExample.Kernel.Kernel).Assembly);
            if (packageInfo != null && !string.IsNullOrEmpty(packageInfo.resolvedPath))
            {
                string path = Path.Combine(
                    packageInfo.resolvedPath,
                    "Runtime",
                    "Resources",
                    "gameplay_catalog_bundle",
                    "bundle.bytes");
                if (File.Exists(path))
                {
                    bundleBytes = File.ReadAllBytes(path);
                    return bundleBytes != null && bundleBytes.Length > 0;
                }
            }
#endif
            bundleBytes = null;
            return false;
        }

        public static string FormatLoadResult(KernelGameplayCatalogLoadResult result)
        {
            string message =
                "version=" +
                result.catalog_version +
                " hash=" +
                result.catalog_hash.ToString("x16") +
                " projectile_templates=" +
                result.projectile_template_count +
                " collider_templates=" +
                result.collider_template_count +
                " collider_bindings=" +
                result.collider_binding_count;

            if (result.status != KernelConstants.GameplayCatalogLoadStatusSuccess)
            {
                if (!string.IsNullOrEmpty(result.diagnostic))
                {
                    message += " error=" + result.diagnostic;
                }

                message +=
                    " error_code=" +
                    result.error_code +
                    " source=" +
                    result.source_kind;

                if (!string.IsNullOrEmpty(result.path))
                {
                    message += " path=" + result.path;
                }

                if (!string.IsNullOrEmpty(result.field))
                {
                    message += " field=" + result.field;
                }

                if (result.line > 0)
                {
                    message += " line=" + result.line;
                }

                if (result.column > 0)
                {
                    message += " column=" + result.column;
                }
            }

            return message;
        }
    }
}
