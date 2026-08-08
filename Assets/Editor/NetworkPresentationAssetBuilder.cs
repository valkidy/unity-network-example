using System.Collections.Generic;
using NetworkExample.UnityDemo.Rendering;
using UnityEditor;
using UnityEngine;

namespace NetworkExample.UnityDemo.Editor
{
    /// <summary>
    /// Builds the primitive stand-in assets for item and prop tests, and upserts
    /// their catalog bindings.
    /// </summary>
    /// <remarks>
    /// Actor bindings are not written here. They belong to the rigs baked by
    /// NetworkActorRigPrefabBuilder, which pairs each entity template with the
    /// prefab built from the skeleton the gameplay catalog says that template
    /// uses. This builder therefore only ever upserts the projectile and prop
    /// bindings it owns and passes the rest of the catalog through untouched --
    /// anything here that rewrote the whole actor table would silently unbind
    /// every rigged actor.
    /// </remarks>
    public static class NetworkPresentationAssetBuilder
    {
        private const string PresentationRoot = "Assets/Presentation";
        private const string MaterialRoot = PresentationRoot + "/Materials";
        private const string PrefabRoot = PresentationRoot + "/Prefabs";
        private const string ResourceRoot = "Assets/Resources";
        private const string CatalogPath = ResourceRoot + "/NetworkPrefabCatalog.asset";

        private const uint FireFloorProjectileTemplateId = 4;
        private const uint StatefulPotionItemTemplateId = 3003;
        private const uint StatefulMagicBottleItemTemplateId = 3004;

        [MenuItem("Network Example/Presentation/Build Item Prop Test Prefabs")]
        public static void BuildItemPropTestPrefabs()
        {
            EnsureFolder("Assets", "Presentation");
            EnsureFolder(PresentationRoot, "Materials");
            EnsureFolder(PresentationRoot, "Prefabs");
            EnsureFolder("Assets", "Resources");

            Material potionMaterial = CreateOrUpdateMaterial(
                MaterialRoot + "/Prop_StatefulPotion.mat",
                new Color(0.22f, 0.85f, 0.42f));
            Material magicBottleMaterial = CreateOrUpdateMaterial(
                MaterialRoot + "/Prop_MagicBottle.mat",
                new Color(0.55f, 0.3f, 1f));
            Material fallbackPropMaterial = CreateOrUpdateMaterial(
                MaterialRoot + "/Prop_Fallback.mat",
                new Color(0.55f, 0.55f, 0.58f));
            Material fireFloorMaterial = CreateOrUpdateMaterial(
                MaterialRoot + "/Projectile_FireFloor.mat",
                new Color(1f, 0.2f, 0.035f));
            ConfigureEmission(fireFloorMaterial, new Color(1f, 0.08f, 0.01f) * 2f);

            GameObject potionPrefab = CreatePropPrefab(
                PrefabRoot + "/Prop_StatefulPotion.prefab",
                "Prop_StatefulPotion",
                PrimitiveType.Capsule,
                new Vector3(0.25f, 0.35f, 0.25f),
                0.35f,
                potionMaterial);
            GameObject magicBottlePrefab = CreatePropPrefab(
                PrefabRoot + "/Prop_MagicBottle.prefab",
                "Prop_MagicBottle",
                PrimitiveType.Cube,
                new Vector3(0.32f, 0.5f, 0.32f),
                0.25f,
                magicBottleMaterial);
            GameObject fallbackPropPrefab = CreatePropPrefab(
                PrefabRoot + "/Prop_Fallback.prefab",
                "Prop_Fallback",
                PrimitiveType.Cube,
                new Vector3(0.4f, 0.4f, 0.4f),
                0.2f,
                fallbackPropMaterial);
            GameObject fireFloorPrefab = CreateFireFloorPrefab(
                PrefabRoot + "/Projectile_FireFloor.prefab",
                fireFloorMaterial);

            NetworkPrefabCatalog catalog =
                AssetDatabase.LoadAssetAtPath<NetworkPrefabCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<NetworkPrefabCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            NetworkPrefabCatalog.ProjectilePrefabBinding[] projectiles =
                UpsertProjectileBinding(
                    catalog.ProjectilePrefabs,
                    FireFloorProjectileTemplateId,
                    fireFloorPrefab);
            NetworkPrefabCatalog.PropPrefabBinding[] props = UpsertPropBindings(
                catalog.PropPrefabs,
                new NetworkPrefabCatalog.PropPrefabBinding(
                    StatefulPotionItemTemplateId,
                    potionPrefab),
                new NetworkPrefabCatalog.PropPrefabBinding(
                    StatefulMagicBottleItemTemplateId,
                    magicBottlePrefab));

            // Preserve actor bindings, fallbacks, and unrelated projectile overrides.
            catalog.Configure(
                catalog.ActorPrefabs,
                projectiles,
                catalog.PlayerActorFallback,
                catalog.AgentActorFallback,
                catalog.ProjectileFallback,
                catalog.EntityFallback);
            catalog.ConfigureProps(
                props,
                catalog.PropFallback != null
                    ? catalog.PropFallback
                    : fallbackPropPrefab);
            EditorUtility.SetDirty(catalog);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(
                "Built item/prop test prefabs and upserted catalog bindings " +
                "without replacing actor presentation overrides.");
        }

        private static GameObject CreatePropPrefab(
            string path,
            string rootName,
            PrimitiveType primitiveType,
            Vector3 visualScale,
            float visualCenterHeight,
            Material material)
        {
            var root = new GameObject(rootName);
            GameObject visual = GameObject.CreatePrimitive(primitiveType);
            visual.name = "Visual";
            RemoveCollider(visual);
            visual.transform.SetParent(root.transform, false);
            visual.transform.localPosition = Vector3.up * visualCenterHeight;
            visual.transform.localScale = visualScale;
            visual.GetComponent<Renderer>().sharedMaterial = material;

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static GameObject CreateFireFloorPrefab(string path, Material material)
        {
            var root = new GameObject("Projectile_FireFloor");
            root.AddComponent<NetworkProjectileView>();

            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            visual.name = "AreaVisual";
            RemoveCollider(visual);
            visual.transform.SetParent(root.transform, false);
            visual.transform.localPosition = Vector3.up * 0.025f;
            // A Unity cylinder is two units wide and two units tall. This produces a
            // two-meter diameter disc whose bottom rests on the authoritative position.
            visual.transform.localScale = new Vector3(1f, 0.025f, 1f);
            visual.GetComponent<Renderer>().sharedMaterial = material;

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static void RemoveCollider(GameObject target)
        {
            Collider collider = target.GetComponent<Collider>();
            if (collider != null)
            {
                Object.DestroyImmediate(collider);
            }
        }

        private static NetworkPrefabCatalog.ProjectilePrefabBinding[]
            UpsertProjectileBinding(
                NetworkPrefabCatalog.ProjectilePrefabBinding[] source,
                uint templateId,
                GameObject prefab)
        {
            var result = new List<NetworkPrefabCatalog.ProjectilePrefabBinding>();
            bool replaced = false;
            if (source != null)
            {
                for (int index = 0; index < source.Length; ++index)
                {
                    NetworkPrefabCatalog.ProjectilePrefabBinding binding = source[index];
                    if (binding.projectileTemplateId == templateId)
                    {
                        if (!replaced)
                        {
                            result.Add(
                                new NetworkPrefabCatalog.ProjectilePrefabBinding(
                                    templateId,
                                    prefab));
                            replaced = true;
                        }
                        continue;
                    }

                    result.Add(binding);
                }
            }

            if (!replaced)
            {
                result.Add(
                    new NetworkPrefabCatalog.ProjectilePrefabBinding(
                        templateId,
                        prefab));
            }

            return result.ToArray();
        }

        private static NetworkPrefabCatalog.PropPrefabBinding[] UpsertPropBindings(
            NetworkPrefabCatalog.PropPrefabBinding[] source,
            params NetworkPrefabCatalog.PropPrefabBinding[] replacements)
        {
            var replacementIds = new HashSet<uint>();
            for (int index = 0; index < replacements.Length; ++index)
            {
                replacementIds.Add(replacements[index].itemTemplateId);
            }

            var result = new List<NetworkPrefabCatalog.PropPrefabBinding>();
            if (source != null)
            {
                for (int index = 0; index < source.Length; ++index)
                {
                    if (!replacementIds.Contains(source[index].itemTemplateId))
                    {
                        result.Add(source[index]);
                    }
                }
            }

            result.AddRange(replacements);
            return result.ToArray();
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

        private static void ConfigureEmission(Material material, Color emissionColor)
        {
            if (material == null || !material.HasProperty("_EmissionColor"))
            {
                return;
            }

            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", emissionColor);
            EditorUtility.SetDirty(material);
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
