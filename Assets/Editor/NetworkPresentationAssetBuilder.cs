using NetworkExample.UnityDemo.Rendering;
using UnityEditor;
using UnityEngine;

namespace NetworkExample.UnityDemo.Editor
{
    public static class NetworkPresentationAssetBuilder
    {
        private const string PresentationRoot = "Assets/Presentation";
        private const string MaterialRoot = PresentationRoot + "/Materials";
        private const string PrefabRoot = PresentationRoot + "/Prefabs";
        private const string ResourceRoot = "Assets/Resources";
        private const string CatalogPath = ResourceRoot + "/NetworkPrefabCatalog.asset";

        [MenuItem("Network Example/Presentation/Rebuild Default Prefabs")]
        public static void BuildDefaults()
        {
            EnsureFolder("Assets", "Presentation");
            EnsureFolder(PresentationRoot, "Materials");
            EnsureFolder(PresentationRoot, "Prefabs");
            EnsureFolder("Assets", "Resources");

            Material playerMaterial = CreateOrUpdateMaterial(
                MaterialRoot + "/PlayerPlaceholder.mat",
                new Color(0.18f, 0.55f, 1f));
            Material agentMaterial = CreateOrUpdateMaterial(
                MaterialRoot + "/AgentPlaceholder.mat",
                new Color(1f, 0.32f, 0.24f));
            Material projectileMaterial = CreateOrUpdateMaterial(
                MaterialRoot + "/ProjectilePlaceholder.mat",
                new Color(1f, 0.86f, 0.2f));

            GameObject playerPrefab = CreateActorPrefab(
                PrefabRoot + "/Actor_Player_Placeholder.prefab",
                "ActorRoot_PlayerPlaceholder",
                0.55f,
                0.35f,
                playerMaterial);
            GameObject agentPrefab = CreateActorPrefab(
                PrefabRoot + "/Actor_Agent_Placeholder.prefab",
                "ActorRoot_AgentPlaceholder",
                0.4f,
                0.4f,
                agentMaterial);
            GameObject projectilePrefab = CreateProjectilePrefab(
                PrefabRoot + "/Projectile_Placeholder.prefab",
                projectileMaterial);

            NetworkPrefabCatalog catalog =
                AssetDatabase.LoadAssetAtPath<NetworkPrefabCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<NetworkPrefabCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            catalog.Configure(
                new[]
                {
                    new NetworkPrefabCatalog.ActorPrefabBinding(1, playerPrefab),
                    new NetworkPrefabCatalog.ActorPrefabBinding(2, agentPrefab),
                },
                new[]
                {
                    new NetworkPrefabCatalog.ProjectilePrefabBinding(2, projectilePrefab),
                    new NetworkPrefabCatalog.ProjectilePrefabBinding(3, projectilePrefab),
                    new NetworkPrefabCatalog.ProjectilePrefabBinding(4, projectilePrefab),
                    new NetworkPrefabCatalog.ProjectilePrefabBinding(5, projectilePrefab),
                    new NetworkPrefabCatalog.ProjectilePrefabBinding(6, projectilePrefab),
                    new NetworkPrefabCatalog.ProjectilePrefabBinding(7, projectilePrefab),
                    new NetworkPrefabCatalog.ProjectilePrefabBinding(8, projectilePrefab),
                },
                playerPrefab,
                agentPrefab,
                projectilePrefab);
            EditorUtility.SetDirty(catalog);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(
                "Rebuilt default network presentation prefabs and catalog at " +
                CatalogPath);
        }

        private static GameObject CreateActorPrefab(
            string path,
            string rootName,
            float capsuleHalfHeight,
            float capsuleRadius,
            Material material)
        {
            var root = new GameObject(rootName);
            root.AddComponent<NetworkActorView>();

            // USER ASSET HOOK: replace this Visual capsule with the authored model and
            // place its Animator here. Keep NetworkActorView on the root and keep the
            // root transform reserved for kernel position/rotation.
            var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name = "Visual";
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            visual.transform.SetParent(root.transform, false);

            float centerHeight = capsuleHalfHeight + capsuleRadius;
            visual.transform.localPosition = Vector3.up * centerHeight;
            visual.transform.localScale = new Vector3(
                capsuleRadius * 2f,
                centerHeight,
                capsuleRadius * 2f);
            visual.GetComponent<Renderer>().sharedMaterial = material;

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static GameObject CreateProjectilePrefab(string path, Material material)
        {
            GameObject projectile = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            projectile.name = "ProjectilePlaceholder";
            Object.DestroyImmediate(projectile.GetComponent<Collider>());
            projectile.transform.localScale = Vector3.one * 0.1f;
            projectile.GetComponent<Renderer>().sharedMaterial = material;
            projectile.AddComponent<NetworkProjectileView>();

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(projectile, path);
            Object.DestroyImmediate(projectile);
            return prefab;
        }

        private static Material CreateOrUpdateMaterial(string path, Color color)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null)
                {
                    shader = Shader.Find("Standard");
                }

                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.color = color;
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void EnsureFolder(string parent, string child)
        {
            string path = parent + "/" + child;
            if (!AssetDatabase.IsValidFolder(path))
            {
                AssetDatabase.CreateFolder(parent, child);
            }
        }
    }
}
