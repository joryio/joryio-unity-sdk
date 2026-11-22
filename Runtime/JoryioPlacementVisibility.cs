using UnityEngine;

namespace Joryio.Runtime
{
    /// <summary>
    /// Viewability for a placement the game draws (D444): put it on the UI
    /// element (a RectTransform) that shows a <see cref="JoryioPlacementItem"/>,
    /// set <see cref="Item"/> - or <see cref="Placement"/> for a held-out
    /// player's empty spot - and it reports MarkDisplayed once at least
    /// <see cref="JoryioSDK.PLACEMENT_VIEWABLE_FRACTION"/> of the rect was on
    /// screen for <see cref="JoryioSDK.IN_APP_DISPLAY_DWELL_MS"/>, continuously,
    /// with the application focused. The same rule as PlacementView on iOS and
    /// Android and insertPlacement on the web.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public sealed class JoryioPlacementVisibility : MonoBehaviour
    {
        /// <summary>The item shown by this element.</summary>
        public JoryioPlacementItem Item { get; set; }

        /// <summary>For a held-out player (IsControl): the empty spot this element stands for.</summary>
        public JoryioPlacement Placement { get; set; }

        /// <summary>The camera of a Screen Space - Camera or World Space canvas; null for Overlay.</summary>
        public Camera UiCamera;

        private RectTransform _rect;
        private float _visibleFor;
        private object _reportedFor;
        private readonly Vector3[] _corners = new Vector3[4];

        private void Awake() => _rect = GetComponent<RectTransform>();

        private void Update()
        {
            object target = Item ?? (object)(Placement != null && Placement.IsControl ? Placement : null);
            if (target == null || ReferenceEquals(target, _reportedFor)) { _visibleFor = 0; return; }
            if (!Application.isFocused || !isActiveAndEnabled || VisibleFraction() < JoryioSDK.PLACEMENT_VIEWABLE_FRACTION)
            {
                _visibleFor = 0;
                return;
            }
            _visibleFor += Time.unscaledDeltaTime;
            if (_visibleFor * 1000f < JoryioSDK.IN_APP_DISPLAY_DWELL_MS) return;
            _reportedFor = target;
            if (Item != null) Item.MarkDisplayed();
            else Placement.MarkDisplayed();
        }

        /// <summary>Share of the rect inside the screen (0 when it has no area).</summary>
        public float VisibleFraction()
        {
            if (_rect == null) return 0;
            _rect.GetWorldCorners(_corners);
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            for (var i = 0; i < 4; i++)
            {
                // Overlay canvas: world space IS screen space; otherwise through the camera.
                Vector2 p = UiCamera == null ? (Vector2)_corners[i] : (Vector2)UiCamera.WorldToScreenPoint(_corners[i]);
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }
            return Fraction(min, max, Screen.width, Screen.height);
        }

        /// <summary>The pure part, testable without a scene.</summary>
        internal static float Fraction(Vector2 min, Vector2 max, float screenWidth, float screenHeight)
        {
            var area = (max.x - min.x) * (max.y - min.y);
            if (area <= 0) return 0;
            var w = Mathf.Max(0, Mathf.Min(max.x, screenWidth) - Mathf.Max(min.x, 0));
            var h = Mathf.Max(0, Mathf.Min(max.y, screenHeight) - Mathf.Max(min.y, 0));
            return w * h / area;
        }
    }
}
