using System;
using System.Collections.Generic;
using Joryio.Runtime;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Joryio.Samples
{
    /// <summary>
    /// A starting point for rendering in-app messages IN YOUR GAME, with your
    /// own art, instead of letting the native SDK draw an OS-styled dialog.
    ///
    /// This is a sample, not part of the SDK: copy it into your project and
    /// restyle it. The look lives in JoryioInAppPanel.prefab, assigned to the
    /// Panel Prefab field - restyle that prefab (or point the field at your
    /// own) and the renderer fills it in. With the field empty, it builds a
    /// plain uGUI panel in code instead, so it has no dependencies beyond
    /// UnityEngine.UI either way.
    ///
    /// WHAT YOU MUST KEEP is the reporting. Installing a handler stops the SDK
    /// drawing, and from that point it cannot see what happened: a message that
    /// is never reported counts as never shown - zero displays in reporting,
    /// nothing for revenue attribution to join on, and frequency caps that never
    /// count it. That is why MarkDisplayed/MarkClicked/MarkDismissed live on the
    /// message object rather than in a documentation note - they are hard to
    /// forget when you already hold the thing.
    /// </summary>
    [AddComponentMenu("Joryio/In-App Renderer (Sample)")]
    public sealed class JoryioInAppRenderer : MonoBehaviour
    {
        // One key PER PLATFORM: each comes from its own app under Settings ->
        // Apps (jry_sdk_ios_..., jry_sdk_android_...). A single key would
        // report every iOS player as Android, or the other way round.
        // Only the current platform's key is read, so the compiler calls the
        // other one unused; a game that ships to both needs both.
#pragma warning disable 0414
        [Header("Your SDK keys and host")]
        [SerializeField] private string iosSdkKey = "jry_sdk_ios_YOUR_IOS_KEY";
        [SerializeField] private string androidSdkKey = "jry_sdk_android_YOUR_ANDROID_KEY";
#pragma warning restore 0414
        [Tooltip("The API host shown for your workspace in the dashboard.")]
        [SerializeField] private string apiHost = "https://api-eu1.joryio.com";

        // The prefab is the thing to restyle. The renderer looks up four
        // children BY NAME, anywhere under the prefab's root, so you can nest
        // them inside your own layout: Title and Body (Text), PrimaryButton and
        // SecondaryButton (Button, each with a Text label inside). The primary
        // button is the template for every campaign button; the secondary one
        // is the close button.
        [Header("Look")]
        [Tooltip("JoryioInAppPanel.prefab, or your own copy of it. Leave empty to build a plain panel in code.")]
        [SerializeField] private GameObject panelPrefab;

        // Used only when no prefab is assigned. With a prefab, the prefab's own
        // colours are the defaults.
        [Tooltip("Code-built panel only: background colour.")]
        [SerializeField] private Color panelColor = new Color(0.04f, 0.07f, 0.25f, 0.97f);
        [Tooltip("Code-built panel only: text colour.")]
        [SerializeField] private Color textColor = Color.white;
        [Tooltip("Code-built panel only: button colour.")]
        [SerializeField] private Color buttonColor = new Color(0f, 0.78f, 0.72f);

        private readonly List<GameObject> _live = new List<GameObject>();

        /// <summary>
        /// The exact delegate this renderer installed, kept so OnDestroy can
        /// remove THAT one and nothing else.
        /// </summary>
        private Action<JoryioInAppMessage> _handler;

        private void Start()
        {
#if UNITY_IOS
            var sdkKey = iosSdkKey;
#else
            var sdkKey = androidSdkKey;
#endif
            JoryioSDK.Initialize(sdkKey, apiHost);

            // canRenderHtml: false - a uGUI canvas cannot display HTML, which
            // needs a web view. This is not cosmetic: campaigns are targeted on
            // what the device reports, and an undeclared capability is read as
            // "cannot", not "unknown". Claiming HTML you cannot draw sends
            // campaigns to players who will never see them.
            _handler = Show;
            JoryioSDK.SetInAppMessageHandler(_handler, canRenderHtml: false);
        }

        private void OnDestroy()
        {
            // This renderer is going away, so nobody would draw the next
            // message. Hand rendering back to the native SDK rather than leave
            // a handler that points at a destroyed object - but only if the
            // installed handler is still OURS. Another script (a second scene's
            // renderer, say) may have installed its own since, and the plain
            // ClearInAppMessageHandler() would remove that one too.
            JoryioSDK.ClearInAppMessageHandler(_handler);
        }

        private void Show(JoryioInAppMessage message)
        {
            // uGUI buttons need an EventSystem to receive taps. A scene without
            // one draws the message and then ignores every touch.
            EnsureEventSystem();

            var canvas = BuildCanvas();
            _live.Add(canvas.gameObject);

            if (!(panelPrefab != null && ShowPrefab(canvas.gameObject, message)))
            {
                ShowBuiltPanel(canvas.gameObject, message);
            }

            // Report AFTER the panel exists, not on receipt. The distinction
            // matters: a display you reported but never drew is a lie the
            // reporting cannot detect.
            message.MarkDisplayed();
        }

        // ── Prefab ───────────────────────────────────────────────────────────

        /// <summary>
        /// Fill an instance of the prefab. False (and nothing left behind) when
        /// the prefab is missing one of the children the renderer fills in, so
        /// the caller can fall back to the code-built panel instead of showing
        /// half a message.
        /// </summary>
        private bool ShowPrefab(GameObject canvas, JoryioInAppMessage message)
        {
            var panel = Instantiate(panelPrefab, canvas.transform, false);
            var title = Child<Text>(panel, "Title");
            var body = Child<Text>(panel, "Body");
            var primary = Child<Button>(panel, "PrimaryButton");
            var secondary = Child<Button>(panel, "SecondaryButton");
            if (title == null || body == null || primary == null || secondary == null)
            {
                Debug.LogWarning(
                    "[Joryio sample] The panel prefab needs children named Title and Body (Text) and " +
                    "PrimaryButton and SecondaryButton (Button); building the plain panel instead.");
                Destroy(panel);
                return false;
            }

            // The author's overrides, where set; the prefab's own look where
            // not. An absent override means INHERIT, so nothing here swaps in a
            // default of its own - see JoryioInAppStyle.
            var style = message.Style;
            var background = panel.GetComponent<Image>();
            if (background != null) ApplyColour(style?.BackgroundColor, c => background.color = c);

            if (!message.IsNative)
            {
                // This sample declared canRenderHtml: false, so the server should
                // never send one here. Say so rather than draw nothing.
                title.text = "This message needs a web view.";
                body.gameObject.SetActive(false);
                primary.gameObject.SetActive(false);
                Wire(secondary, () => Dismiss(canvas, message));
                return true;
            }

            // Title and Body are the AUTHORED copy. The campaign's Name is its
            // dashboard label and never something to show a player, so a
            // message with no title simply has no title line.
            title.text = message.Title ?? string.Empty;
            title.gameObject.SetActive(!string.IsNullOrEmpty(message.Title));
            body.text = message.Body ?? string.Empty;
            body.gameObject.SetActive(!string.IsNullOrEmpty(message.Body));

            ApplyColour(style?.TextColor, c => { title.color = c; body.color = c; });
            if (style?.FontSize.HasValue == true)
            {
                // FontSize is the BODY size; the headline scales from it so the
                // author's one number keeps the hierarchy.
                body.fontSize = Mathf.RoundToInt(style.FontSize.Value);
                title.fontSize = Mathf.RoundToInt(style.FontSize.Value * 1.4f);
            }
            if (style?.TitleWeight != null)
            {
                title.fontStyle = style.TitleWeight == "bold" || style.TitleWeight == "semibold"
                    ? FontStyle.Bold
                    : FontStyle.Normal;
            }

            // One copy of the primary button per campaign button, in order,
            // where the template sits. The template itself is then hidden.
            var at = primary.transform.GetSiblingIndex();
            for (var i = 0; i < message.Buttons.Length; i++)
            {
                var button = message.Buttons[i];
                var copy = Instantiate(primary.gameObject, primary.transform.parent, false);
                copy.name = "Button " + (string.IsNullOrEmpty(button.Id) ? i.ToString() : button.Id);
                copy.transform.SetSiblingIndex(at + i);
                var label = copy.GetComponentInChildren<Text>(true);
                if (label != null)
                {
                    label.text = button.Text;
                    ApplyColour(style?.PrimaryButtonTextColor, c => label.color = c);
                }
                var image = copy.GetComponent<Image>();
                if (image != null) ApplyColour(style?.PrimaryButtonColor, c => image.color = c);
                Wire(copy.GetComponent<Button>(), () => OnButton(canvas, message, button));
            }
            primary.gameObject.SetActive(false);

            var showClose = message.Buttons.Length == 0 || message.CloseButton;
            secondary.gameObject.SetActive(showClose);
            if (showClose) Wire(secondary, () => Dismiss(canvas, message));
            return true;
        }

        private static T Child<T>(GameObject root, string name) where T : Component
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name) return t.GetComponent<T>();
            }
            return null;
        }

        private static void Wire(Button button, UnityEngine.Events.UnityAction onClick)
        {
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(onClick);
        }

        // ── Shared ───────────────────────────────────────────────────────────

        /// <summary>
        /// The campaign's own buttons. The SDK cannot see a button you draw, so
        /// acting on it is yours: open the URL, ask for push permission, or just
        /// close - and report the tap either way.
        /// </summary>
        private void OnButton(GameObject canvas, JoryioInAppMessage message, JoryioInAppButton button)
        {
            message.MarkClicked(button);
            if ((button.Action == "url" || button.Action == "deep_link") && !string.IsNullOrEmpty(button.Url))
            {
                Application.OpenURL(button.Url);
            }
            else if (button.Action == "request_push_permission")
            {
                // A push primer: the author's button IS the ask. Nothing
                // happens unless the renderer makes the call.
                JoryioSDK.RequestPushPermission(granted =>
                    Debug.Log(granted ? "[Joryio sample] Push enabled" : "[Joryio sample] Push declined"));
            }
            // The tap closes the message, so it is ALSO a dismissal: report
            // both, as the native SDKs do when they draw the message (clicked,
            // then dismissed). Reporting only the click left every clicked
            // message without a dismissal in the funnel.
            Dismiss(canvas, message);
        }

        private void Dismiss(GameObject canvas, JoryioInAppMessage message)
        {
            message.MarkDismissed();
            _live.Remove(canvas);
            Destroy(canvas);
        }

        /// <summary>
        /// Create an EventSystem when the scene has none, with the input module
        /// that matches the project's Active Input Handling.
        ///
        /// The Input System module is found by name rather than referenced,
        /// so this file compiles in a project without that package. With only
        /// the new Input System active, the legacy StandaloneInputModule would
        /// throw every frame - so it is used only when the legacy input manager
        /// is on. link.xml beside this file keeps the module from being
        /// stripped from a release build.
        /// </summary>
        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM
            var module = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (module != null)
            {
                go.AddComponent(module);
                return;
            }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER || !ENABLE_INPUT_SYSTEM
            go.AddComponent<StandaloneInputModule>();
#else
            Debug.LogWarning(
                "[Joryio sample] The Input System is active but its UI input module was not found, " +
                "so message buttons will not respond. Add an EventSystem with an InputSystemUIInputModule to the scene.");
#endif
        }

        private Canvas BuildCanvas()
        {
            var go = new GameObject("JoryioInAppCanvas");
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Above the game's own UI. A message the player cannot see is a
            // message that reports a display and delivers nothing.
            canvas.sortingOrder = 32000;
            go.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            go.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        // ── Code-built fallback ──────────────────────────────────────────────

        private void ShowBuiltPanel(GameObject canvas, JoryioInAppMessage message)
        {
            var panel = BuildPanel(canvas.transform, message);
            if (message.IsNative)
            {
                foreach (var button in message.Buttons)
                {
                    var captured = button;
                    AddButton(panel.transform, captured.Text, message.Style, () => OnButton(canvas, message, captured));
                }
            }
            if (!message.IsNative || message.Buttons.Length == 0 || message.CloseButton)
            {
                AddButton(panel.transform, "Close", () => Dismiss(canvas, message));
            }
        }

        private GameObject BuildPanel(Transform parent, JoryioInAppMessage message)
        {
            var panel = new GameObject("Panel", typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            panel.transform.SetParent(parent, false);
            var style = message.Style;
            panel.GetComponent<Image>().color = Colour(style?.BackgroundColor, panelColor);

            // Full width less a margin, and as tall as its content, centred.
            // A fixed-height panel squeezes its labels to nothing once a
            // campaign has more than a button or two.
            var rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.1f, 0.5f);
            rect.anchorMax = new Vector2(0.9f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            panel.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var layout = panel.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(24, 24, 24, 24);
            layout.spacing = 12;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;

            if (!message.IsNative)
            {
                AddLabel(panel.transform, "This message needs a web view.", 22);
                return panel;
            }
            var body = style?.FontSize.HasValue == true ? Mathf.RoundToInt(style.FontSize.Value) : 20;
            var fg = Colour(style?.TextColor, textColor);
            if (!string.IsNullOrEmpty(message.Title))
            {
                AddLabel(panel.transform, message.Title, Mathf.RoundToInt(body * 1.4f), fg,
                    style?.TitleWeight == "bold" || style?.TitleWeight == "semibold");
            }
            if (!string.IsNullOrEmpty(message.Body)) AddLabel(panel.transform, message.Body, body, fg, false);
            return panel;
        }

        /// <summary>
        /// A CSS colour string from the campaign ("#1F2937", "#RGBA"), or the
        /// fallback when absent or unparseable. Unity's parser takes the hex
        /// forms; a named or rgb() colour falls back rather than guessing.
        /// </summary>
        private static Color Colour(string css, Color fallback)
        {
            if (string.IsNullOrEmpty(css)) return fallback;
            return ColorUtility.TryParseHtmlString(css, out var parsed) ? parsed : fallback;
        }

        /// <summary>Apply an author colour only when one is set and parses; otherwise leave the prefab's own.</summary>
        private static void ApplyColour(string css, Action<Color> apply)
        {
            if (!string.IsNullOrEmpty(css) && ColorUtility.TryParseHtmlString(css, out var parsed)) apply(parsed);
        }

        private void AddLabel(Transform parent, string content, int size)
        {
            AddLabel(parent, content, size, textColor, false);
        }

        private void AddLabel(Transform parent, string content, int size, Color colour, bool bold)
        {
            var go = new GameObject("Label", typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.text = content;
            text.fontSize = size;
            text.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            text.color = colour;
            text.alignment = TextAnchor.MiddleCenter;
            // The built-in font was renamed in 2022.2. Asking 2021.3 for
            // LegacyRuntime.ttf returns null and every label renders blank.
#if UNITY_2022_2_OR_NEWER
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
#else
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
#endif
        }

        private void AddButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick)
        {
            AddButton(parent, label, null, onClick);
        }

        private void AddButton(Transform parent, string label, JoryioInAppStyle style, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(label, typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = Colour(style?.PrimaryButtonColor, buttonColor);
            go.GetComponent<Button>().onClick.AddListener(onClick);
            go.AddComponent<LayoutElement>().minHeight = 56;
            AddLabel(go.transform, label, 22, Colour(style?.PrimaryButtonTextColor, textColor), false);
            // Stretch the label over the button, so a long label wraps across
            // the button's width rather than inside a 100-unit default box.
            var labelRect = (RectTransform)go.transform.GetChild(0);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
        }
    }
}
