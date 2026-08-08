using NetworkExample.Kernel;

namespace NetworkExample.UnityDemo.Common
{
    /// <summary>
    /// Resolves the skeleton layouts the demo's rigs are built from.
    /// </summary>
    /// <remarks>
    /// Bone names, parents and the content hash live in the skeleton manifests
    /// inside the gameplay catalog bundle, not in a table compiled into the
    /// kernel package. Everything that builds or checks a rig has to read them
    /// from <see cref="KernelSkeletonManifestCatalog"/>, so a skeleton can be
    /// rebuilt and redeployed without a matching Unity change.
    /// </remarks>
    public static class NetworkSkeletonManifests
    {
        public const uint MonsterSimSkeletonAssetId = 1u;
        public const string MonsterSimSkeletonName = "simplified_monster_sim_v4";

        /// <summary>
        /// Replaces the loaded manifests with the ones in <paramref name="bundleBytes"/>.
        /// Call this with the same bytes the host loads, so the rig Unity draws
        /// and the skeleton the kernel poses cannot be two different versions.
        /// </summary>
        public static bool TryLoad(byte[] bundleBytes, out string error)
        {
            return KernelSkeletonManifestCatalog.TryLoadFromBundle(bundleBytes, out error);
        }

        /// <summary>
        /// Loads the packaged bundle's manifests when nothing has loaded any yet,
        /// for editor tooling that builds a rig outside a running session.
        /// </summary>
        public static bool TryEnsureLoaded(out string error)
        {
            if (KernelSkeletonManifestCatalog.Manifests.Count > 0)
            {
                error = null;
                return true;
            }
            if (!NetworkGameplayCatalogBundle.TryLoadDefault(out byte[] bundleBytes, out _))
            {
                error =
                    "No gameplay catalog bundle was found, so no skeleton manifest " +
                    "could be loaded.";
                return false;
            }
            return TryLoad(bundleBytes, out error);
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
    }
}
