using UnityEngine;

namespace Joryio.Runtime
{
    /// <summary>
    /// An in-app message the GAME renders, whose lifecycle does the reporting.
    ///
    /// This is deliberately not a plain data object plus a documentation note
    /// asking you to remember to call TrackInAppImpression. When the SDK draws a
    /// message it also reports the display, the click and the dismissal. When
    /// the game draws it, the SDK can see none of that: the handoff emits no
    /// in_app.displayed, and the frequency budget is spent only when a display
    /// is reported (SDK contract addendum 17). An unreported campaign therefore
    /// reads as delivered to nobody, breaks revenue attribution (which joins on
    /// that event), and is never counted by its frequency caps. It fails
    /// silently, and it looks like a targeting problem rather than an
    /// integration one.
    ///
    /// Three SDKs have now walked into this. Putting the reporting on an object
    /// the game already holds is the only version of the design where forgetting
    /// is not the default.
    /// </summary>
    public sealed class JoryioInAppMessage
    {
        public string Id { get; }
        /// <summary>The campaign's name in the dashboard. Internal - not copy to show.</summary>
        public string Name { get; }
        /// <summary>modal | banner | slideup | fullscreen | custom.</summary>
        public string Type { get; }

        /// <summary>
        /// "native" or "html". Decides which of the fields below are set.
        ///
        /// A native message is structured data - Title, Body, ImageUrl, Buttons -
        /// and is what a Unity canvas can draw. An HTML message is a document
        /// (Html + Css) that needs a web view; declare
        /// <c>canRenderHtml: true</c> in SetInAppMessageHandler only if you
        /// host one, or the server will not send HTML campaigns here at all.
        /// </summary>
        public string Kind { get; }

        // Native content. Title and ImageUrl may be null; Body is always set.
        public string Title { get; }
        public string Body { get; }
        public string ImageUrl { get; }
        /// <summary>Whether the author asked for a close affordance.</summary>
        public bool CloseButton { get; }
        /// <summary>Whether tapping outside the message should dismiss it.</summary>
        public bool BackdropDismissible { get; }
        /// <summary>Never null; empty when the campaign has no buttons.</summary>
        public JoryioInAppButton[] Buttons { get; }
        /// <summary>
        /// The author's presentation overrides, or null when the campaign set
        /// none - and null means INHERIT your game's own styling, which is why
        /// a message with no overrides looks like the rest of your UI. Native
        /// content only.
        /// </summary>
        public JoryioInAppStyle Style { get; }

        // HTML content. Null for a native message.
        public string Html { get; }
        public string Css { get; }
        /// <summary>
        /// HTML modal / slideup only: the widest the message may be, in
        /// points. Give your web view min(MaxWidth, screen width - your
        /// margins) BEFORE the content lays out, so the author's media
        /// queries see the real width, and let only the height follow the
        /// content - measured from document.body (with
        /// body{display:flow-root}), not the root element, whose scrollHeight
        /// never drops below the web view's own height. Null for every other
        /// message.
        /// </summary>
        public float? MaxWidth { get; }

        public bool IsNative => Kind == "native";
        public bool IsHtml => Kind == "html";

        private bool _reported;
        private bool _displayed;

        internal JoryioInAppMessage(
            string id,
            string name,
            string type,
            string kind,
            string title,
            string body,
            string imageUrl,
            bool closeButton,
            bool backdropDismissible,
            JoryioInAppButton[] buttons,
            JoryioInAppStyle style,
            string html,
            string css,
            float? maxWidth)
        {
            Id = id;
            Name = name;
            Type = type;
            Kind = kind;
            Title = title;
            Body = body;
            ImageUrl = imageUrl;
            CloseButton = closeButton;
            BackdropDismissible = backdropDismissible;
            Buttons = buttons ?? new JoryioInAppButton[0];
            Style = style;
            Html = html;
            Css = css;
            MaxWidth = maxWidth;
        }

        /// <summary>
        /// Convenience for renderers that need a message as it arrived. The
        /// payload used to carry only Id and Name, so a handler had nothing to
        /// draw but the campaign's internal name (audit 2026-09-26, U25).
        /// </summary>
        public override string ToString() => $"{Kind} in-app '{Name}' ({Id})";

        /// <summary>
        /// Call once the message is actually on screen. Reported once per
        /// message however often it is called: a display is what frequency caps
        /// and attribution count, so a renderer that redraws (a resize, a
        /// scene reload) must not turn one message into two impressions.
        /// </summary>
        public void MarkDisplayed()
        {
            _reported = true;
            if (_displayed) return;
            _displayed = true;
            JoryioSDK.TrackInAppImpression(Id, "displayed");
        }

        /// <summary>
        /// Call when the player activates a button or link. Pass the button
        /// they tapped: its Id is reported as the click's <c>elementId</c>, so
        /// the campaign's click map can tell the buttons apart. A button with
        /// no Id reports no elementId - never its label, which is display copy
        /// (translated, edited) and would split one button into several.
        /// </summary>
        public void MarkClicked(JoryioInAppButton button = null)
        {
            _reported = true;
            var elementId = button == null || string.IsNullOrEmpty(button.Id) ? null : button.Id;
            JoryioSDK.TrackInAppImpression(Id, "clicked", elementId);
        }

        /// <summary>Call when the message leaves the screen.</summary>
        public void MarkDismissed()
        {
            _reported = true;
            JoryioSDK.TrackInAppImpression(Id, "dismissed");
        }

        /// <summary>
        /// Last chance to notice. A message that is collected having never been
        /// marked is an integration that is losing display data right now, and
        /// nothing else in the system can tell - the server sees an SDK that
        /// synced campaigns and never showed one, which is indistinguishable
        /// from an audience nobody matched.
        /// </summary>
        ~JoryioInAppMessage()
        {
            if (_reported) return;
            Debug.LogWarning(
                $"[Joryio] In-app message '{Name}' ({Id}) was never reported. " +
                "Call MarkDisplayed() when you render it (and MarkClicked/MarkDismissed as the " +
                "player acts), or campaign analytics will show zero displays and frequency caps " +
                "will not count this message.");
        }
    }

    /// <summary>
    /// Presentation overrides the campaign author set on a native message.
    ///
    /// Every property is optional, and an absent one (null) means INHERIT your
    /// game's own styling. Apply the ones that are present; do not substitute
    /// defaults of your own for the ones that are not. Same fields, same names
    /// and same meaning as the React Native SDK's <c>message.style</c>.
    ///
    /// <see cref="FontFamily"/> is best effort: apply it only if your game
    /// ships that font, and keep your own typeface when it does not -
    /// substituting an arbitrary font looks worse than ignoring the override.
    /// </summary>
    public sealed class JoryioInAppStyle
    {
        /// <summary>CSS colour string, e.g. "#1F2937".</summary>
        public string BackgroundColor { get; }
        public string TextColor { get; }
        public string PrimaryButtonColor { get; }
        public string PrimaryButtonTextColor { get; }
        public float? CornerRadius { get; }
        /// <summary>Body size; scale the headline from it to keep the hierarchy.</summary>
        public float? FontSize { get; }
        /// <summary>regular | medium | semibold | bold - the HEADLINE's weight.</summary>
        public string TitleWeight { get; }
        /// <summary>
        /// auto | start | center | end. <c>auto</c> (or absent) means align by
        /// the MESSAGE's own language, not the device locale - one workspace
        /// sends Hebrew and English to the same game.
        /// </summary>
        public string TextAlign { get; }
        /// <summary>Apply only if your game ships it; otherwise keep your own.</summary>
        public string FontFamily { get; }

        internal JoryioInAppStyle(
            string backgroundColor,
            string textColor,
            string primaryButtonColor,
            string primaryButtonTextColor,
            float? cornerRadius,
            float? fontSize,
            string titleWeight,
            string textAlign,
            string fontFamily)
        {
            BackgroundColor = backgroundColor;
            TextColor = textColor;
            PrimaryButtonColor = primaryButtonColor;
            PrimaryButtonTextColor = primaryButtonTextColor;
            CornerRadius = cornerRadius;
            FontSize = fontSize;
            TitleWeight = titleWeight;
            TextAlign = textAlign;
            FontFamily = fontFamily;
        }
    }

    /// <summary>
    /// A button on a native in-app message.
    ///
    /// <see cref="Action"/> is the wire spelling shared with React Native:
    /// <c>dismiss</c>, <c>url</c>, <c>deep_link</c> or
    /// <c>request_push_permission</c>. The SDK does not act on it for a
    /// game-rendered message - it cannot see your button - so open
    /// <see cref="Url"/> yourself for <c>url</c> / <c>deep_link</c>, call
    /// <c>JoryioSDK.RequestPushPermission</c> for the primer action, and report
    /// the tap through <see cref="JoryioInAppMessage.MarkClicked"/>.
    /// </summary>
    public sealed class JoryioInAppButton
    {
        public string Id { get; }
        public string Text { get; }
        public string Action { get; }
        /// <summary>Set for url / deep_link actions; null otherwise.</summary>
        public string Url { get; }

        internal JoryioInAppButton(string id, string text, string action, string url)
        {
            Id = id;
            Text = text;
            Action = action;
            Url = url;
        }
    }
}
