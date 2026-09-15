using NetworkExample.UnityDemo.Text3D;
using UnityEditor;
using UnityEngine;

namespace NetworkExample.UnityDemo.EditorTools
{
    internal static class WaxGlyphTextMenu
    {
        private const string MaterialPath = "Assets/Resources/Test/WaxCandyV7/Materials/WaxCandyGlyph.mat";

        [MenuItem("GameObject/3D Object/Wax Glyph Text", false, 10)]
        private static void CreateWaxGlyphText(MenuCommand command)
        {
            var gameObject = new GameObject("Wax Glyph Text");
            var glyphText = gameObject.AddComponent<WaxGlyphText>();
            glyphText.TemplateMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            GameObjectUtility.SetParentAndAlign(gameObject, command.context as GameObject);

            // Glyphs are built with capitals 1.9 units tall to match WaxCandy's tuning.
            gameObject.transform.localScale = Vector3.one * 0.5f;
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Wax Glyph Text");
            Selection.activeObject = gameObject;
        }
    }
}
