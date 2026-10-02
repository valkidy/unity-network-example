using UnityEngine;
using UnityEngine.UI;

namespace NetworkExample.UnityDemo.UI
{
    /// <summary>
    /// The rest UI: shown while the local player is inside a building, with how
    /// long the building has left and how much damage it can still take.
    /// </summary>
    /// <remarks>
    /// Built from untextured Images and legacy Text at runtime, as the reticle
    /// is, so it needs no prefab or art. It only draws what it is told: opening
    /// and closing follow the local shelter state, which the runner owns, so a
    /// refused activation never opens it and being thrown out by a collapse
    /// closes it the same way leaving does.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ShelterRestView : MonoBehaviour
    {
        /// <summary>The open_ui id of the rest UI (action_open_rest_ui).</summary>
        public const uint RestUiId = 1;

        [SerializeField]
        private int sortingOrder = 110;

        [SerializeField]
        private Vector2 panelSize = new Vector2(320f, 120f);

        [SerializeField]
        [Min(0f)]
        private float bottomMargin = 64f;

        [SerializeField]
        private Color panelColor = new Color(0f, 0f, 0f, 0.6f);

        [SerializeField]
        private Color hpFillColor = new Color(0.45f, 0.85f, 0.4f, 1f);

        [SerializeField]
        private Color hpLowColor = new Color(1f, 0.35f, 0.25f, 1f);

        [SerializeField]
        [Range(0f, 1f)]
        private float hpLowFraction = 0.3f;

        [SerializeField]
        private string leaveKeyLabel = "F";

        private RectTransform root;
        private Text titleText;
        private Text countdownText;
        private Text hpText;
        private Text hintText;
        private RectTransform hpFill;
        private Image hpFillImage;

        public bool IsOpen => root != null && root.gameObject.activeSelf;

        /// <summary>
        /// Ticks until a building that spawned on <paramref name="spawnTick"/>
        /// and lives <paramref name="lifetimeTicks"/> expires, as of
        /// <paramref name="currentTick"/>. Never negative.
        /// </summary>
        public static uint RemainingTicks(uint spawnTick, uint lifetimeTicks, uint currentTick)
        {
            // Unsigned arithmetic, so a tick counter that wraps still counts down.
            uint elapsed = currentTick - spawnTick;
            return elapsed >= lifetimeTicks ? 0u : lifetimeTicks - elapsed;
        }

        /// <summary>"2:05" for 125 seconds; rounds up, so 0:00 is the collapse.</summary>
        public static string FormatCountdown(uint remainingTicks, uint tickRate)
        {
            uint rate = tickRate == 0 ? 1u : tickRate;
            uint seconds = (remainingTicks + rate - 1) / rate;
            return (seconds / 60) + ":" + (seconds % 60).ToString("00");
        }

        public void Open(uint uiId)
        {
            EnsureBuilt();
            titleText.text = uiId == RestUiId ? "Resting" : "Inside (ui " + uiId + ")";
            hintText.text = "[" + leaveKeyLabel + "] Leave";
            root.gameObject.SetActive(true);
        }

        public void Close()
        {
            if (root != null)
            {
                root.gameObject.SetActive(false);
            }
        }

        /// <param name="hasBuilding">
        /// False while the building's render state has not arrived; the panel
        /// keeps its last numbers rather than flashing empty.
        /// </param>
        /// <param name="remainingTicks">Ignored when <paramref name="expires"/> is false.</param>
        public void SetStatus(
            bool hasBuilding,
            bool expires,
            uint remainingTicks,
            uint tickRate,
            int hp,
            int maxHp)
        {
            if (!IsOpen || !hasBuilding)
            {
                return;
            }

            countdownText.text = expires
                ? "Collapses in " + FormatCountdown(remainingTicks, tickRate)
                : string.Empty;

            float fraction = maxHp > 0 ? Mathf.Clamp01((float)hp / maxHp) : 0f;
            hpFill.anchorMax = new Vector2(fraction, 1f);
            hpFillImage.color = fraction <= hpLowFraction ? hpLowColor : hpFillColor;
            hpText.text = "Tent " + Mathf.Max(0, hp) + " / " + Mathf.Max(0, maxHp);
        }

        private void EnsureBuilt()
        {
            if (root != null)
            {
                return;
            }

            GameObject canvasObject = new GameObject("Shelter Rest Canvas");
            canvasObject.transform.SetParent(transform, false);
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            root = CreateRect(canvas.transform, "Rest Panel");
            root.anchorMin = new Vector2(0.5f, 0f);
            root.anchorMax = new Vector2(0.5f, 0f);
            root.pivot = new Vector2(0.5f, 0f);
            root.sizeDelta = panelSize;
            root.anchoredPosition = new Vector2(0f, bottomMargin);
            AddImage(root, panelColor);

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            titleText = CreateText(root, "Title", font, 20, FontStyle.Bold, 0.78f, 1f);
            countdownText = CreateText(root, "Countdown", font, 16, FontStyle.Normal, 0.56f, 0.78f);

            RectTransform hpBar = CreateRect(root, "HP Bar");
            hpBar.anchorMin = new Vector2(0.08f, 0.32f);
            hpBar.anchorMax = new Vector2(0.92f, 0.5f);
            hpBar.sizeDelta = Vector2.zero;
            AddImage(hpBar, new Color(1f, 1f, 1f, 0.15f));

            hpFill = CreateRect(hpBar, "Fill");
            hpFill.anchorMin = Vector2.zero;
            hpFill.anchorMax = Vector2.one;
            hpFill.pivot = new Vector2(0f, 0.5f);
            hpFill.sizeDelta = Vector2.zero;
            hpFillImage = AddImage(hpFill, hpFillColor);

            hpText = CreateText(hpBar, "HP", font, 12, FontStyle.Normal, 0f, 1f);
            hintText = CreateText(root, "Hint", font, 14, FontStyle.Normal, 0.04f, 0.28f);

            root.gameObject.SetActive(false);
        }

        private static RectTransform CreateRect(Transform parent, string name)
        {
            GameObject child = new GameObject(name, typeof(RectTransform));
            child.transform.SetParent(parent, false);
            return (RectTransform)child.transform;
        }

        private static Image AddImage(RectTransform rect, Color color)
        {
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>A full-width text band between two vertical anchors.</summary>
        private static Text CreateText(
            RectTransform parent,
            string name,
            Font font,
            int size,
            FontStyle style,
            float anchorMinY,
            float anchorMaxY)
        {
            RectTransform rect = CreateRect(parent, name);
            rect.anchorMin = new Vector2(0f, anchorMinY);
            rect.anchorMax = new Vector2(1f, anchorMaxY);
            rect.sizeDelta = Vector2.zero;
            Text text = rect.gameObject.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.raycastTarget = false;
            return text;
        }
    }
}
