using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

// NetworkExample.UnityDemo.Client shadows UnityEditor.PackageManager.Client from inside
// the NetworkExample.UnityDemo.Editor namespace, so the upm client needs an explicit alias.
using UpmClient = UnityEditor.PackageManager.Client;

namespace NetworkExample.UnityDemo.Editor
{
    /// <summary>
    /// Forces UPM to re-fetch the git hosted kernel package. Mirrors the manual workflow:
    /// drop the packages-lock.json entry so the resolver stops pinning the old commit,
    /// delete Library/PackageCache/&lt;package&gt;@&lt;hash&gt; so nothing is reused from disk,
    /// then ask UPM to add the url from Packages/manifest.json again.
    ///
    /// The url is always read back from manifest.json instead of being hard coded, so
    /// switching between refs (#dev-latest, #tag-v0.1, a different fork, ...) keeps working.
    /// </summary>
    public static class NetworkKernelPackageUpdater
    {
        public const string PackageName = "com.network-example.kernel";

        internal const string MenuRoot = "Tools/Network Example/Kernel Package/";
        private const string ManifestRelativePath = "Packages/manifest.json";
        private const string LockRelativePath = "Packages/packages-lock.json";
        private const string PackageCacheRelativePath = "Library/PackageCache";

        private static AddRequest s_PendingRequest;

        internal static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

        internal static string ManifestPath => Path.Combine(ProjectRoot, ManifestRelativePath);

        internal static string LockPath => Path.Combine(ProjectRoot, LockRelativePath);

        internal static string PackageCacheRoot => Path.Combine(ProjectRoot, PackageCacheRelativePath);

        internal static bool IsBusy => s_PendingRequest != null;

        [MenuItem(MenuRoot + "Update From Git", false, 100)]
        public static void UpdateFromGitMenu()
        {
            string url = ReadManifestUrl();
            if (string.IsNullOrEmpty(url))
            {
                EditorUtility.DisplayDialog(
                    "Kernel Package",
                    $"'{PackageName}' was not found in {ManifestRelativePath}.\n\n" +
                    "Use 'Change Git Source...' to point the project at a git url first.",
                    "OK");
                return;
            }

            bool confirmed = EditorUtility.DisplayDialog(
                "Update Kernel Package",
                $"Re-fetch {PackageName} from:\n\n{url}\n\n" +
                $"This removes its entry from {LockRelativePath} and deletes its " +
                $"{PackageCacheRelativePath} folder, then re-resolves the package.",
                "Update",
                "Cancel");

            if (!confirmed)
            {
                return;
            }

            ForceRefetch(url);
        }

        [MenuItem(MenuRoot + "Update From Git", true)]
        private static bool UpdateFromGitMenuValidate() => !IsBusy;

        [MenuItem(MenuRoot + "Change Git Source...", false, 101)]
        public static void ChangeGitSourceMenu() => NetworkKernelPackageSourceWindow.Open();

        [MenuItem(MenuRoot + "Log Current Source", false, 200)]
        public static void LogCurrentSourceMenu()
        {
            string url = ReadManifestUrl();
            string lockedHash = ReadLockedHash();
            List<string> cacheFolders = FindPackageCacheFolders();

            Debug.Log(
                $"[{PackageName}]\n" +
                $"  manifest url : {(string.IsNullOrEmpty(url) ? "<not declared>" : url)}\n" +
                $"  locked hash  : {(string.IsNullOrEmpty(lockedHash) ? "<none>" : lockedHash)}\n" +
                $"  package cache: {(cacheFolders.Count == 0 ? "<empty>" : string.Join(", ", cacheFolders.ConvertAll(Path.GetFileName)))}");
        }

        /// <summary>
        /// Clears the lock entry plus the on-disk cache and re-adds <paramref name="url"/>.
        /// Also writes <paramref name="url"/> back to the manifest so a changed ref sticks.
        /// </summary>
        internal static void ForceRefetch(string url)
        {
            if (IsBusy)
            {
                Debug.LogWarning($"[{PackageName}] an update is already running.");
                return;
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                Debug.LogError($"[{PackageName}] refusing to update: the git url is empty.");
                return;
            }

            url = url.Trim();

            try
            {
                AssetDatabase.SaveAssets();

                int deleted = DeletePackageCacheFolders();
                bool lockCleared = RemoveLockEntry();
                WriteManifestUrl(url);

                Debug.Log(
                    $"[{PackageName}] refetching {url} " +
                    $"(lock entry {(lockCleared ? "removed" : "absent")}, {deleted} cache folder(s) deleted).");
            }
            catch (Exception exception)
            {
                Debug.LogError($"[{PackageName}] failed to prepare the update: {exception}");
                return;
            }

            s_PendingRequest = UpmClient.Add(url);
            EditorApplication.update += PollPendingRequest;
        }

        private static void PollPendingRequest()
        {
            AddRequest request = s_PendingRequest;
            if (request == null)
            {
                EditorApplication.update -= PollPendingRequest;
                return;
            }

            if (!request.IsCompleted)
            {
                return;
            }

            EditorApplication.update -= PollPendingRequest;
            s_PendingRequest = null;

            if (request.Status == StatusCode.Success)
            {
                Debug.Log($"[{PackageName}] resolved {request.Result.packageId} at {request.Result.resolvedPath}");
                AssetDatabase.Refresh();
            }
            else
            {
                Debug.LogError($"[{PackageName}] update failed: {request.Error?.message ?? "unknown error"}");
            }
        }

        // ---------------------------------------------------------------- manifest

        /// <summary>Returns the git url declared for the package, or null when absent.</summary>
        internal static string ReadManifestUrl()
        {
            if (!File.Exists(ManifestPath))
            {
                return null;
            }

            Match match = Regex.Match(File.ReadAllText(ManifestPath), BuildStringValuePattern(PackageName));
            return match.Success ? Unescape(match.Groups["value"].Value) : null;
        }

        /// <summary>Writes the git url into the manifest, adding the dependency when missing.</summary>
        internal static void WriteManifestUrl(string url)
        {
            string json = File.ReadAllText(ManifestPath);
            string escaped = Escape(url);
            string pattern = BuildStringValuePattern(PackageName);

            if (Regex.IsMatch(json, pattern))
            {
                string updated = Regex.Replace(json, pattern, match =>
                {
                    Group value = match.Groups["value"];
                    return match.Value.Substring(0, value.Index - match.Index)
                        + escaped
                        + match.Value.Substring(value.Index - match.Index + value.Length);
                });

                if (updated != json)
                {
                    File.WriteAllText(ManifestPath, updated);
                }

                return;
            }

            Match dependencies = Regex.Match(json, "\"dependencies\"\\s*:\\s*\\{");
            if (!dependencies.Success)
            {
                throw new InvalidOperationException($"No \"dependencies\" object found in {ManifestRelativePath}.");
            }

            int insertAt = dependencies.Index + dependencies.Length;
            string entry = $"\n    \"{PackageName}\": \"{escaped}\",";
            File.WriteAllText(ManifestPath, json.Insert(insertAt, entry));
        }

        // ------------------------------------------------------------------- lock

        /// <summary>Returns the commit hash packages-lock.json currently pins, or null.</summary>
        internal static string ReadLockedHash()
        {
            if (!File.Exists(LockPath))
            {
                return null;
            }

            string json = File.ReadAllText(LockPath);
            if (!TryFindEntry(json, PackageName, out int start, out int end))
            {
                return null;
            }

            Match hash = Regex.Match(json.Substring(start, end - start), "\"hash\"\\s*:\\s*\"(?<value>[^\"]*)\"");
            return hash.Success ? hash.Groups["value"].Value : null;
        }

        /// <summary>Deletes the package's packages-lock.json entry. Returns false when it was not there.</summary>
        internal static bool RemoveLockEntry()
        {
            if (!File.Exists(LockPath))
            {
                return false;
            }

            string json = File.ReadAllText(LockPath);
            if (!TryRemoveEntry(json, PackageName, out string stripped))
            {
                return false;
            }

            File.WriteAllText(LockPath, stripped);
            return true;
        }

        // ------------------------------------------------------------------ cache

        /// <summary>All Library/PackageCache folders belonging to the package (the @hash suffix varies).</summary>
        internal static List<string> FindPackageCacheFolders()
        {
            var folders = new List<string>();
            if (!Directory.Exists(PackageCacheRoot))
            {
                return folders;
            }

            foreach (string directory in Directory.GetDirectories(PackageCacheRoot))
            {
                string name = Path.GetFileName(directory);
                if (name.Equals(PackageName, StringComparison.Ordinal)
                    || name.StartsWith(PackageName + "@", StringComparison.Ordinal))
                {
                    folders.Add(directory);
                }
            }

            return folders;
        }

        private static int DeletePackageCacheFolders()
        {
            int deleted = 0;
            foreach (string folder in FindPackageCacheFolders())
            {
                ClearReadOnlyAttributes(folder);
                Directory.Delete(folder, true);
                deleted++;

                string meta = folder + ".meta";
                if (File.Exists(meta))
                {
                    File.Delete(meta);
                }
            }

            return deleted;
        }

        /// <summary>Git checkouts under PackageCache are read-only, which blocks Directory.Delete.</summary>
        private static void ClearReadOnlyAttributes(string root)
        {
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                FileAttributes attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }
        }

        // ------------------------------------------------------------- json utils

        private static string BuildStringValuePattern(string key)
            => "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(?<value>(?:[^\"\\\\]|\\\\.)*)\"";

        /// <summary>
        /// Locates an object valued entry, spanning from the key to its closing brace. String valued
        /// occurrences are skipped, so a nested "package": "1.2.3" version constraint never matches.
        /// </summary>
        private static bool TryFindEntry(string json, string key, out int start, out int end)
        {
            string token = "\"" + key + "\"";
            start = -1;
            end = -1;

            int open = -1;
            for (int candidate = json.IndexOf(token, StringComparison.Ordinal);
                 candidate >= 0;
                 candidate = json.IndexOf(token, candidate + token.Length, StringComparison.Ordinal))
            {
                int cursor = candidate + token.Length;
                while (cursor < json.Length && char.IsWhiteSpace(json[cursor]))
                {
                    cursor++;
                }

                if (cursor >= json.Length || json[cursor] != ':')
                {
                    continue;
                }

                cursor++;
                while (cursor < json.Length && char.IsWhiteSpace(json[cursor]))
                {
                    cursor++;
                }

                if (cursor < json.Length && json[cursor] == '{')
                {
                    start = candidate;
                    open = cursor;
                    break;
                }
            }

            if (start < 0)
            {
                return false;
            }

            int depth = 0;
            bool inString = false;
            for (int i = open; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                switch (c)
                {
                    case '"':
                        inString = true;
                        break;
                    case '{':
                        depth++;
                        break;
                    case '}':
                        if (--depth == 0)
                        {
                            end = i + 1;
                            return true;
                        }

                        break;
                }
            }

            start = -1;
            return false;
        }

        /// <summary>Removes an object valued entry along with the comma that keeps the json valid.</summary>
        private static bool TryRemoveEntry(string json, string key, out string result)
        {
            result = json;
            if (!TryFindEntry(json, key, out int start, out int end))
            {
                return false;
            }

            // Grow backwards over the indentation so the removal is line aligned.
            while (start > 0 && (json[start - 1] == ' ' || json[start - 1] == '\t'))
            {
                start--;
            }

            if (end < json.Length && json[end] == ',')
            {
                // Not the last entry: drop the trailing comma and the newline behind it.
                end++;
                if (end < json.Length && json[end] == '\r')
                {
                    end++;
                }

                if (end < json.Length && json[end] == '\n')
                {
                    end++;
                }
            }
            else
            {
                // Last entry: the comma that separates it from the previous one has to go instead.
                int previous = start - 1;
                while (previous >= 0 && char.IsWhiteSpace(json[previous]))
                {
                    previous--;
                }

                if (previous >= 0 && json[previous] == ',')
                {
                    start = previous;
                }
            }

            result = json.Remove(start, end - start);
            return true;
        }

        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static string Unescape(string value) => value.Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    /// <summary>
    /// Small editor window for retargeting the kernel package at a different git url or ref
    /// without hand editing Packages/manifest.json.
    /// </summary>
    internal sealed class NetworkKernelPackageSourceWindow : EditorWindow
    {
        private string _repository = string.Empty;
        private string _reference = string.Empty;
        private string _lockedHash;
        private string _cacheFolders;

        internal static void Open()
        {
            var window = GetWindow<NetworkKernelPackageSourceWindow>(true, "Kernel Package Source", true);
            window.minSize = new Vector2(520f, 260f);
            window.Reload();
        }

        private void OnEnable() => Reload();

        private void Reload()
        {
            SplitUrl(NetworkKernelPackageUpdater.ReadManifestUrl(), out _repository, out _reference);
            _lockedHash = NetworkKernelPackageUpdater.ReadLockedHash();

            List<string> folders = NetworkKernelPackageUpdater.FindPackageCacheFolders();
            _cacheFolders = folders.Count == 0
                ? "<empty>"
                : string.Join("\n", folders.ConvertAll(Path.GetFileName));
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField(NetworkKernelPackageUpdater.PackageName, EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUILayout.HelpBox(
                "Repository accepts the full upm git url including its ?path= query. " +
                "Ref is the part after '#' (branch, tag or commit) and may be left empty.",
                MessageType.None);

            _repository = EditorGUILayout.TextField("Repository", _repository);
            _reference = EditorGUILayout.TextField("Ref", _reference);

            string composed = ComposeUrl(_repository, _reference);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Resulting url", EditorStyles.miniBoldLabel);
            EditorGUILayout.SelectableLabel(composed, EditorStyles.textArea, GUILayout.Height(34f));

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Locked hash", string.IsNullOrEmpty(_lockedHash) ? "<none>" : _lockedHash);
                EditorGUILayout.TextField("Package cache", _cacheFolders);
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh"))
                {
                    Reload();
                }

                GUILayout.FlexibleSpace();

                bool canUpdate = !NetworkKernelPackageUpdater.IsBusy && !string.IsNullOrWhiteSpace(_repository);
                using (new EditorGUI.DisabledScope(!canUpdate))
                {
                    if (GUILayout.Button("Apply & Update", GUILayout.Width(140f)))
                    {
                        NetworkKernelPackageUpdater.ForceRefetch(composed);
                        Close();
                    }
                }
            }
        }

        private static void SplitUrl(string url, out string repository, out string reference)
        {
            repository = url ?? string.Empty;
            reference = string.Empty;
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            int hash = url.IndexOf('#');
            if (hash < 0)
            {
                return;
            }

            repository = url.Substring(0, hash);
            reference = url.Substring(hash + 1);
        }

        private static string ComposeUrl(string repository, string reference)
        {
            repository = (repository ?? string.Empty).Trim();
            reference = (reference ?? string.Empty).Trim();
            return string.IsNullOrEmpty(reference) ? repository : repository + "#" + reference;
        }
    }
}
