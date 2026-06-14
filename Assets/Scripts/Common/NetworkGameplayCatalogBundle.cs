using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.Common
{
    public static class NetworkGameplayCatalogBundle
    {
        public const string DefaultResourcePath = "gameplay_catalog_bundle/bundle";
        public const string DefaultEntryPath = "gameplay_catalog.yaml";

        public static bool TryLoadDefault(out byte[] bundleBytes, out string entryPath)
        {
            entryPath = DefaultEntryPath;

            TextAsset bundleAsset = Resources.Load<TextAsset>(DefaultResourcePath);
            if (bundleAsset == null)
            {
                bundleBytes = null;
                Debug.LogError(
                    "Gameplay catalog bundle not found at Resources/" +
                    DefaultResourcePath +
                    ".bytes. Build game_server/gameplay_catalog_bundle:bundle.zip " +
                    "and copy it to Assets/Resources/gameplay_catalog_bundle/bundle.bytes.");
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
