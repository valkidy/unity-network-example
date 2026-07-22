using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using NetworkExample.Kernel;
using UnityEngine;
#if UNITY_EDITOR
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

        public static bool TryReadPlayerWeaponLoadout(
            byte[] bundleBytes,
            string entryPath,
            out byte[] weaponIds,
            out int activeWeaponSlot,
            out string diagnostic)
        {
            weaponIds = null;
            activeWeaponSlot = 0;
            diagnostic = null;
            if (bundleBytes == null || bundleBytes.Length == 0)
            {
                diagnostic = "Gameplay catalog bundle is empty.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(entryPath))
            {
                diagnostic = "Gameplay catalog entry path is empty.";
                return false;
            }

            try
            {
                using (var stream = new MemoryStream(bundleBytes, false))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
                {
                    if (!TryReadTextEntry(
                            archive,
                            entryPath,
                            out string catalogYaml,
                            out diagnostic))
                    {
                        return false;
                    }
                    if (!TryReadTopLevelScalar(
                            catalogYaml,
                            "entity_template_dir",
                            out string entityTemplateDirectory) ||
                        !TryReadNestedScalar(
                            catalogYaml,
                            "player",
                            "entity_template",
                            out string playerTemplateName))
                    {
                        diagnostic =
                            "Gameplay catalog does not declare the player entity template.";
                        return false;
                    }

                    string playerTemplatePath = BuildEntityTemplatePath(
                        entityTemplateDirectory,
                        playerTemplateName);
                    if (!TryReadTextEntry(
                            archive,
                            playerTemplatePath,
                            out string playerTemplateYaml,
                            out diagnostic))
                    {
                        return false;
                    }
                    if (!TryReadByteSequence(
                            playerTemplateYaml,
                            "weapon_slots",
                            out weaponIds,
                            out diagnostic))
                    {
                        return false;
                    }
                    if (weaponIds.Length == 0 ||
                        weaponIds.Length > KernelConstants.MaxWeaponSlots)
                    {
                        diagnostic =
                            "Player weapon_slots must contain between 1 and " +
                            KernelConstants.MaxWeaponSlots +
                            " weapon IDs.";
                        weaponIds = null;
                        return false;
                    }
                    if (!TryReadTopLevelScalar(
                            playerTemplateYaml,
                            "active_weapon_slot",
                            out string activeSlotText) ||
                        !int.TryParse(
                            activeSlotText,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out activeWeaponSlot) ||
                        activeWeaponSlot < 0 ||
                        activeWeaponSlot >= weaponIds.Length)
                    {
                        diagnostic =
                            "Player active_weapon_slot is missing or outside weapon_slots.";
                        weaponIds = null;
                        activeWeaponSlot = 0;
                        return false;
                    }

                    return true;
                }
            }
            catch (Exception exception)
            {
                weaponIds = null;
                activeWeaponSlot = 0;
                diagnostic = "Gameplay catalog loadout read failed: " + exception.Message;
                return false;
            }
        }

        public static bool TryLoadSynchronizedBundle(
            string cacheDirectory,
            string serverAddress,
            KernelGameplayCatalogManifest manifest,
            out byte[] bundleBytes,
            out string diagnostic)
        {
            bundleBytes = null;
            diagnostic = null;
            if (!HasValidManifestDigest(manifest))
            {
                diagnostic = "Gameplay catalog sync manifest has an invalid bundle digest.";
                return false;
            }

            if (TryLoadDefault(out byte[] defaultBytes, out _) &&
                BundleMatchesManifest(defaultBytes, manifest))
            {
                bundleBytes = defaultBytes;
                return true;
            }

            if (string.IsNullOrWhiteSpace(cacheDirectory))
            {
                diagnostic =
                    "The synchronized gameplay catalog is not the packaged default and " +
                    "no cache directory is configured.";
                return false;
            }

            try
            {
                string path = GetSynchronizedBundlePath(
                    cacheDirectory,
                    serverAddress,
                    manifest);
                if (!File.Exists(path))
                {
                    diagnostic = "Synchronized gameplay catalog bundle was not cached at " + path;
                    return false;
                }

                byte[] cachedBytes = File.ReadAllBytes(path);
                if (!BundleMatchesManifest(cachedBytes, manifest))
                {
                    diagnostic =
                        "Cached gameplay catalog bundle does not match the server manifest.";
                    return false;
                }

                bundleBytes = cachedBytes;
                return true;
            }
            catch (Exception exception)
            {
                diagnostic =
                    "Synchronized gameplay catalog bundle read failed: " + exception.Message;
                return false;
            }
        }

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

        private static bool TryReadTextEntry(
            ZipArchive archive,
            string path,
            out string text,
            out string diagnostic)
        {
            text = null;
            diagnostic = null;
            string normalizedPath = NormalizeArchivePath(path);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                diagnostic = "Gameplay catalog archive path is invalid: " + path;
                return false;
            }

            ZipArchiveEntry entry = archive.GetEntry(normalizedPath);
            if (entry == null)
            {
                diagnostic =
                    "Gameplay catalog archive entry was not found: " + normalizedPath;
                return false;
            }

            using (Stream entryStream = entry.Open())
            using (var reader = new StreamReader(
                       entryStream,
                       Encoding.UTF8,
                       true,
                       1024,
                       false))
            {
                text = reader.ReadToEnd();
            }
            return true;
        }

        private static string BuildEntityTemplatePath(string directory, string templateName)
        {
            string normalizedDirectory = NormalizeArchivePath(Unquote(directory));
            string normalizedName = NormalizeArchivePath(Unquote(templateName));
            if (string.IsNullOrEmpty(normalizedDirectory) ||
                string.IsNullOrEmpty(normalizedName))
            {
                return string.Empty;
            }
            if (!Path.HasExtension(normalizedName))
            {
                normalizedName += ".yaml";
            }
            return normalizedDirectory.TrimEnd('/') + "/" + normalizedName.TrimStart('/');
        }

        private static string NormalizeArchivePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string normalized = path.Trim().Replace('\\', '/').TrimStart('/');
            string[] segments = normalized.Split('/');
            for (int index = 0; index < segments.Length; ++index)
            {
                if (segments[index] == ".." || segments[index].Length == 0)
                {
                    return string.Empty;
                }
            }
            return normalized;
        }

        private static bool TryReadTopLevelScalar(
            string yaml,
            string key,
            out string value)
        {
            string[] lines = SplitLines(yaml);
            for (int index = 0; index < lines.Length; ++index)
            {
                if (Indentation(lines[index]) != 0 ||
                    !TrySplitYamlField(lines[index], out string field, out string fieldValue) ||
                    field != key ||
                    string.IsNullOrEmpty(fieldValue))
                {
                    continue;
                }

                value = Unquote(fieldValue);
                return true;
            }

            value = null;
            return false;
        }

        private static bool TryReadNestedScalar(
            string yaml,
            string parentKey,
            string key,
            out string value)
        {
            string[] lines = SplitLines(yaml);
            int parentIndent = -1;
            for (int index = 0; index < lines.Length; ++index)
            {
                int indent = Indentation(lines[index]);
                if (parentIndent < 0)
                {
                    if (indent == 0 &&
                        TrySplitYamlField(
                            lines[index],
                            out string parentField,
                            out string parentValue) &&
                        parentField == parentKey &&
                        string.IsNullOrEmpty(parentValue))
                    {
                        parentIndent = indent;
                    }
                    continue;
                }

                if (IsBlankOrComment(lines[index]))
                {
                    continue;
                }
                if (indent <= parentIndent)
                {
                    break;
                }
                if (TrySplitYamlField(lines[index], out string field, out string fieldValue) &&
                    field == key &&
                    !string.IsNullOrEmpty(fieldValue))
                {
                    value = Unquote(fieldValue);
                    return true;
                }
            }

            value = null;
            return false;
        }

        private static bool TryReadByteSequence(
            string yaml,
            string key,
            out byte[] values,
            out string diagnostic)
        {
            values = null;
            diagnostic = null;
            string[] lines = SplitLines(yaml);
            for (int index = 0; index < lines.Length; ++index)
            {
                if (Indentation(lines[index]) != 0 ||
                    !TrySplitYamlField(lines[index], out string field, out string fieldValue) ||
                    field != key)
                {
                    continue;
                }

                var parsed = new List<byte>();
                if (!string.IsNullOrEmpty(fieldValue))
                {
                    string inline = fieldValue.Trim();
                    if (inline.Length < 2 || inline[0] != '[' || inline[inline.Length - 1] != ']')
                    {
                        diagnostic = key + " must be a YAML sequence.";
                        return false;
                    }
                    string contents = inline.Substring(1, inline.Length - 2).Trim();
                    if (contents.Length > 0)
                    {
                        string[] items = contents.Split(',');
                        for (int itemIndex = 0; itemIndex < items.Length; ++itemIndex)
                        {
                            if (!TryAddWeaponId(items[itemIndex], parsed, out diagnostic))
                            {
                                return false;
                            }
                        }
                    }
                }
                else
                {
                    for (++index; index < lines.Length; ++index)
                    {
                        if (IsBlankOrComment(lines[index]))
                        {
                            continue;
                        }
                        if (Indentation(lines[index]) == 0)
                        {
                            break;
                        }

                        string item = StripYamlComment(lines[index]).Trim();
                        if (!item.StartsWith("-", StringComparison.Ordinal))
                        {
                            diagnostic = key + " contains an invalid sequence item.";
                            return false;
                        }
                        if (!TryAddWeaponId(item.Substring(1), parsed, out diagnostic))
                        {
                            return false;
                        }
                    }
                }

                values = parsed.ToArray();
                return true;
            }

            diagnostic = "Player entity template does not declare " + key + ".";
            return false;
        }

        private static bool TryAddWeaponId(
            string text,
            List<byte> values,
            out string diagnostic)
        {
            string scalar = Unquote(StripYamlComment(text).Trim());
            if (!byte.TryParse(
                    scalar,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out byte weaponId))
            {
                diagnostic = "Invalid weapon ID in weapon_slots: " + scalar;
                return false;
            }
            if (values.Contains(weaponId))
            {
                diagnostic = "Duplicate weapon ID in weapon_slots: " + weaponId;
                return false;
            }

            values.Add(weaponId);
            diagnostic = null;
            return true;
        }

        private static bool TrySplitYamlField(
            string line,
            out string field,
            out string value)
        {
            string content = StripYamlComment(line).Trim();
            int separator = content.IndexOf(':');
            if (separator <= 0)
            {
                field = null;
                value = null;
                return false;
            }

            field = content.Substring(0, separator).Trim();
            value = content.Substring(separator + 1).Trim();
            return true;
        }

        private static string StripYamlComment(string line)
        {
            bool inSingleQuote = false;
            bool inDoubleQuote = false;
            for (int index = 0; index < line.Length; ++index)
            {
                char character = line[index];
                if (character == '\'' && !inDoubleQuote)
                {
                    inSingleQuote = !inSingleQuote;
                }
                else if (character == '"' && !inSingleQuote)
                {
                    inDoubleQuote = !inDoubleQuote;
                }
                else if (character == '#' && !inSingleQuote && !inDoubleQuote)
                {
                    return line.Substring(0, index);
                }
            }
            return line;
        }

        private static string Unquote(string value)
        {
            string trimmed = value.Trim();
            if (trimmed.Length >= 2 &&
                ((trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"') ||
                 (trimmed[0] == '\'' && trimmed[trimmed.Length - 1] == '\'')))
            {
                return trimmed.Substring(1, trimmed.Length - 2);
            }
            return trimmed;
        }

        private static string[] SplitLines(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private static int Indentation(string line)
        {
            int count = 0;
            while (count < line.Length && line[count] == ' ')
            {
                count++;
            }
            return count;
        }

        private static bool IsBlankOrComment(string line)
        {
            string trimmed = line.Trim();
            return trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal);
        }

        private static bool HasValidManifestDigest(KernelGameplayCatalogManifest manifest)
        {
            return manifest.bundle_size > 0 &&
                manifest.bundle_sha256 != null &&
                manifest.bundle_sha256.Length == KernelConstants.GameplayCatalogSha256Size;
        }

        private static bool BundleMatchesManifest(
            byte[] bundleBytes,
            KernelGameplayCatalogManifest manifest)
        {
            if (bundleBytes == null ||
                bundleBytes.LongLength != manifest.bundle_size ||
                !HasValidManifestDigest(manifest))
            {
                return false;
            }

            byte[] digest;
            using (SHA256 sha256 = SHA256.Create())
            {
                digest = sha256.ComputeHash(bundleBytes);
            }
            int difference = 0;
            for (int index = 0; index < digest.Length; ++index)
            {
                difference |= digest[index] ^ manifest.bundle_sha256[index];
            }
            return difference == 0;
        }

        private static string GetSynchronizedBundlePath(
            string cacheDirectory,
            string serverAddress,
            KernelGameplayCatalogManifest manifest)
        {
            string normalizedAddress = (serverAddress ?? string.Empty).Trim().ToLowerInvariant();
            string identitySource =
                normalizedAddress + "\n" + (manifest.content_namespace ?? string.Empty);
            byte[] identityDigest;
            using (SHA256 sha256 = SHA256.Create())
            {
                identityDigest = sha256.ComputeHash(Encoding.UTF8.GetBytes(identitySource));
            }

            return Path.Combine(
                Path.GetFullPath(cacheDirectory),
                ToHex(identityDigest),
                manifest.catalog_hash.ToString("x16"),
                "bundle.zip");
        }

        private static string ToHex(byte[] bytes)
        {
            var result = new StringBuilder(bytes.Length * 2);
            for (int index = 0; index < bytes.Length; ++index)
            {
                result.Append(bytes[index].ToString("x2"));
            }
            return result.ToString();
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
