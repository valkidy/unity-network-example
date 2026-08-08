using System.Collections.Generic;
using NetworkExample.Kernel;

namespace NetworkExample.UnityDemo.Common
{
    /// <summary>
    /// Resolves the skeleton layouts the demo's rigs are built from.
    /// </summary>
    /// <remarks>
    /// Bone names, parents, rest pose and content hash live in the skeleton
    /// manifests inside the gameplay catalog bundle, not in a table compiled into
    /// the kernel package, and which skeleton an entity template is rigged to is
    /// authored next to that template. Everything that builds or checks a rig
    /// reads both out of the bundle the server sent, so a rig can be added,
    /// renamed or rebuilt on the server without a matching Unity change.
    /// </remarks>
    public static class NetworkSkeletonManifests
    {
        /// <summary>
        /// The one rig with a locomotion capture golden behind it, kept as the
        /// control the newer template rigs are compared against.
        /// </summary>
        public const uint MonsterSimSkeletonAssetId = 1u;
        public const string MonsterSimSkeletonName = "simplified_monster_sim_v4";

        private static readonly Dictionary<uint, uint> SkeletonAssetIdByTemplateId =
            new Dictionary<uint, uint>();

        /// <summary>
        /// Which skeleton asset each entity template is rigged to, as read from
        /// the last loaded bundle. Empty until <see cref="TryLoad"/> succeeds.
        /// </summary>
        public static IReadOnlyDictionary<uint, uint> SkeletonAssetIdByTemplate =>
            SkeletonAssetIdByTemplateId;

        /// <summary>
        /// Replaces the loaded manifests and template pairings with the ones in
        /// <paramref name="bundleBytes"/>. Call this with the same bytes the host
        /// loads, so the rig Unity draws and the skeleton the kernel poses cannot
        /// be two different versions.
        /// </summary>
        public static bool TryLoad(byte[] bundleBytes, string entryPath, out string error)
        {
            if (!KernelSkeletonManifestCatalog.TryLoadFromBundle(bundleBytes, out error))
            {
                return false;
            }
            return TryLoadTemplatePairings(bundleBytes, entryPath, out error);
        }

        /// <summary>
        /// Loads the packaged bundle when nothing has loaded any manifests yet,
        /// for editor tooling that builds a rig outside a running session.
        /// </summary>
        public static bool TryEnsureLoaded(out string error)
        {
            if (KernelSkeletonManifestCatalog.Manifests.Count > 0)
            {
                error = null;
                return true;
            }
            if (!NetworkGameplayCatalogBundle.TryLoadDefault(
                    out byte[] bundleBytes,
                    out string entryPath))
            {
                error =
                    "No gameplay catalog bundle was found, so no skeleton manifest " +
                    "could be loaded.";
                return false;
            }
            return TryLoad(bundleBytes, entryPath, out error);
        }

        /// <summary>
        /// The manifest for whatever skeleton <paramref name="templateId"/> is
        /// rigged to, or false for the templates that carry no skeleton at all.
        /// </summary>
        public static bool TryGetForTemplate(
            uint templateId,
            out KernelSkeletonManifest manifest,
            out string error)
        {
            manifest = null;
            if (!TryEnsureLoaded(out error))
            {
                return false;
            }
            if (!SkeletonAssetIdByTemplateId.TryGetValue(
                    templateId,
                    out uint skeletonAssetId))
            {
                error =
                    "Entity template " + templateId +
                    " declares no skeleton in the loaded gameplay catalog.";
                return false;
            }
            if (!KernelSkeletonManifestCatalog.TryGet(skeletonAssetId, out manifest))
            {
                error =
                    "Entity template " + templateId + " is rigged to skeleton asset " +
                    skeletonAssetId + ", which the loaded catalog has no manifest for.";
                return false;
            }
            error = null;
            return true;
        }

        public static bool TryGetMonsterSim(
            out KernelSkeletonManifest manifest,
            out string error)
        {
            return TryGet(
                MonsterSimSkeletonAssetId,
                MonsterSimSkeletonName,
                out manifest,
                out error);
        }

        /// <summary>
        /// Looks up a manifest and holds it against the name the caller expects,
        /// so a catalog that renumbered its skeletons fails here rather than
        /// silently building a rig for the wrong one.
        /// </summary>
        public static bool TryGet(
            uint assetId,
            string expectedName,
            out KernelSkeletonManifest manifest,
            out string error)
        {
            manifest = null;
            if (!TryEnsureLoaded(out error))
            {
                return false;
            }
            if (!KernelSkeletonManifestCatalog.TryGet(assetId, out manifest))
            {
                error =
                    "The gameplay catalog carries no skeleton manifest for asset " +
                    assetId + " (" + expectedName + ").";
                return false;
            }
            if (manifest.Name != expectedName)
            {
                error =
                    "Skeleton asset " + assetId + " is '" + manifest.Name +
                    "' in this catalog, expected '" + expectedName + "'.";
                manifest = null;
                return false;
            }
            error = null;
            return true;
        }

        private static bool TryLoadTemplatePairings(
            byte[] bundleBytes,
            string entryPath,
            out string error)
        {
            SkeletonAssetIdByTemplateId.Clear();
            if (!NetworkGameplayCatalogBundle.TryReadTemplateSkeletonNames(
                    bundleBytes,
                    entryPath,
                    out Dictionary<uint, string> manifestNameByTemplateId,
                    out error))
            {
                return false;
            }

            var assetIdByName = new Dictionary<string, uint>();
            foreach (KeyValuePair<uint, KernelSkeletonManifest> pair in
                     KernelSkeletonManifestCatalog.Manifests)
            {
                assetIdByName[pair.Value.Name] = pair.Key;
            }

            foreach (KeyValuePair<uint, string> pair in manifestNameByTemplateId)
            {
                if (!assetIdByName.TryGetValue(pair.Value, out uint assetId))
                {
                    error =
                        "Entity template " + pair.Key + " is rigged to '" + pair.Value +
                        "', which the bundle ships no skeleton manifest for.";
                    SkeletonAssetIdByTemplateId.Clear();
                    return false;
                }
                SkeletonAssetIdByTemplateId[pair.Key] = assetId;
            }

            error = null;
            return true;
        }
    }
}
