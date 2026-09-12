using NetworkExample.UnityDemo.CameraSystem;
using UnityEngine;
using UnityEngine.UI;

namespace NetworkExample.UnityDemo.UI
{
    /// <summary>
    /// Draws the aim reticle. The whole thing is built from untextured Images at
    /// runtime -- four ticks around a gap, plus an optional centre dot -- so the
    /// HUD needs no art assets and no prefab to exist before it can be shown.
    /// </summary>
    /// <remarks>
    /// The reticle is fixed to the screen, not free to roam it: the player turns
    /// the camera to move the aim, exactly as the over-the-shoulder reference does.
    /// Fixed does not mean centred, though -- it sits wherever
    /// <see cref="ThirdPersonFollowCamera.CurrentReticleViewportPoint"/> says, and
    /// the camera derives its aim direction through that same point.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class AimReticleView : MonoBehaviour
    {
        [SerializeField]
        [Min(0f)]
        private float tickLength = 10f;

        [SerializeField]
        [Min(0.5f)]
        private float tickThickness = 2f;

        /// <summary>Distance from the reticle centre to the inner end of a tick.</summary>
        [SerializeField]
        [Min(0f)]
        private float hipGap = 22f;

        [SerializeField]
        [Min(0f)]
        private float aimGap = 9f;

        [SerializeField]
        [Range(0f, 1f)]
        private float hipAlpha = 0.35f;

        [SerializeField]
        [Range(0f, 1f)]
        private float aimAlpha = 0.9f;

        [SerializeField]
        private Color reticleColor = Color.white;

        [SerializeField]
        private bool showCenterDot = true;

        [SerializeField]
        private int sortingOrder = 100;

        private ThirdPersonFollowCamera followCamera;
        private RectTransform root;
        private RectTransform[] ticks;
        private RectTransform centerDot;
        private Graphic[] graphics;

        public RectTransform Root => root;
        public float CurrentGap { get; private set; }
        public float CurrentAlpha { get; private set; }

        public void Configure(ThirdPersonFollowCamera camera)
        {
            followCamera = camera;
            EnsureBuilt();
        }

        private void OnEnable()
        {
            EnsureBuilt();
        }

        private void LateUpdate()
        {
            if (followCamera == null)
            {
                return;
            }

            UpdateReticle(
                followCamera.AimBlend,
                followCamera.CurrentReticleViewportPoint);
        }

        /// <summary>
        /// Places and styles the reticle for one frame. Public so the placement can
        /// be driven straight from a test without a camera or a player loop.
        /// </summary>
        public void UpdateReticle(float aimBlend, Vector2 viewportPoint)
        {
            EnsureBuilt();
            float blend = Mathf.Clamp01(aimBlend);
            CurrentGap = Mathf.Lerp(hipGap, aimGap, blend);
            CurrentAlpha = Mathf.Lerp(hipAlpha, aimAlpha, blend);

            // Anchors are already normalized viewport coordinates, so the reticle
            // can be placed by anchor alone and stays put across resolutions.
            root.anchorMin = viewportPoint;
            root.anchorMax = viewportPoint;
            root.anchoredPosition = Vector2.zero;

            float offset = CurrentGap + tickLength * 0.5f;
            SetTick(ticks[0], new Vector2(0f, offset), new Vector2(tickThickness, tickLength));
            SetTick(ticks[1], new Vector2(0f, -offset), new Vector2(tickThickness, tickLength));
            SetTick(ticks[2], new Vector2(-offset, 0f), new Vector2(tickLength, tickThickness));
            SetTick(ticks[3], new Vector2(offset, 0f), new Vector2(tickLength, tickThickness));

            if (centerDot != null)
            {
                centerDot.gameObject.SetActive(showCenterDot);
                centerDot.sizeDelta = new Vector2(tickThickness, tickThickness);
                centerDot.anchoredPosition = Vector2.zero;
            }

            Color color = reticleColor;
            color.a = CurrentAlpha;
            for (int index = 0; index < graphics.Length; ++index)
            {
                graphics[index].color = color;
            }
        }

        private static void SetTick(RectTransform tick, Vector2 position, Vector2 size)
        {
            tick.anchoredPosition = position;
            tick.sizeDelta = size;
        }

        private void EnsureBuilt()
        {
            if (root != null)
            {
                return;
            }

            Canvas canvas = GetComponentInChildren<Canvas>();
            if (canvas == null)
            {
                GameObject canvasObject = new GameObject("Aim Reticle Canvas");
                canvasObject.transform.SetParent(transform, false);
                canvas = canvasObject.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = sortingOrder;
                CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            }

            root = CreateChild(canvas.transform, "Reticle");
            ticks = new RectTransform[4];
            ticks[0] = CreateImage(root, "Up");
            ticks[1] = CreateImage(root, "Down");
            ticks[2] = CreateImage(root, "Left");
            ticks[3] = CreateImage(root, "Right");
            centerDot = CreateImage(root, "Dot");

            graphics = new Graphic[]
            {
                ticks[0].GetComponent<Graphic>(),
                ticks[1].GetComponent<Graphic>(),
                ticks[2].GetComponent<Graphic>(),
                ticks[3].GetComponent<Graphic>(),
                centerDot.GetComponent<Graphic>(),
            };
        }

        private static RectTransform CreateChild(Transform parent, string name)
        {
            GameObject child = new GameObject(name, typeof(RectTransform));
            child.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)child.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
            return rect;
        }

        private static RectTransform CreateImage(Transform parent, string name)
        {
            RectTransform rect = CreateChild(parent, name);
            // An Image with no sprite draws a plain quad in the default UI
            // material, which is all a tick or a dot needs.
            Image image = rect.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            return rect;
        }
    }
}
