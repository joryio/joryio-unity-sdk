using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;

namespace Joryio.Runtime
{
    /// <summary>
    /// Receives UnitySendMessage callbacks from the native bridges.
    ///
    /// UnitySendMessage addresses a GameObject BY NAME and can only carry a
    /// single string, which is why every payload is JSON. It is the path every
    /// comparable vendor uses, and it delivers on the main thread, which is what
    /// makes it safe to touch the scene from here.
    ///
    /// Created hidden and DontDestroyOnLoad: a scene change that destroyed this
    /// object would silently stop every callback, and the symptom - in-app
    /// messages that work until the player leaves the menu - is a miserable one
    /// to trace.
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class JoryioCallbackReceiver : MonoBehaviour
    {
        internal const string ObjectName = "JoryioCallbackReceiver";

        internal static Action<JoryioInAppMessage> OnInAppMessageHandler;

        // Pending answers for the calls that are async on one platform and
        // synchronous on the other. Keyed by request id so two in-flight calls
        // cannot resolve into each other's handler - a bug that would only ever
        // appear under a double tap, which is the worst kind to chase.
        //
        // LOCKED: a game may call RequestPushPermission, RegisterPushToken or
        // SubmitInAppForm from a worker thread while an answer is being
        // delivered on the main thread, and an unsynchronised Dictionary
        // corrupts under that (two ids, a lost entry, or an exception inside
        // the SDK) (scan 5, N7). The handler itself runs outside the lock.
        private static readonly object PendingLock = new object();
        private static readonly Dictionary<int, Action<string>> Pending = new Dictionary<int, Action<string>>();
        private static int _nextRequestId = 1;

        internal static int Register(Action<string> handler)
        {
            lock (PendingLock)
            {
                var id = _nextRequestId++;
                Pending[id] = handler;
                return id;
            }
        }

        /// <summary>Drop a pending answer whose native call never went out.</summary>
        internal static void Forget(int requestId)
        {
            lock (PendingLock) Pending.Remove(requestId);
        }

        /// <summary>Number of answers still awaited. Tests.</summary>
        internal static int PendingCount
        {
            get { lock (PendingLock) return Pending.Count; }
        }

        /// <summary>Deliver an answer. Used by native and by the Editor stub.</summary>
        internal static void Resolve(int requestId, string value)
        {
            Action<string> handler;
            lock (PendingLock)
            {
                if (!Pending.TryGetValue(requestId, out handler)) return;
                Pending.Remove(requestId);
            }
            try { handler(value); }
            catch (Exception e) { Debug.LogError($"[Joryio] Callback threw: {e}"); }
        }

        private static JoryioCallbackReceiver _instance;

        internal static void Ensure()
        {
            if (_instance != null) return;
            var go = new GameObject(ObjectName);
            // HideInHierarchy, NOT HideAndDontSave. UnitySendMessage finds its
            // target by name among the live scene objects, the same lookup as
            // GameObject.Find - and HideAndDontSave includes DontSave, which
            // keeps an object out of that lookup in some Unity versions. Every
            // callback would then be dropped with only a "SendMessage: object
            // JoryioCallbackReceiver not found!" line in the device log. The
            // object only needs to be out of the way, not out of the scene.
            // (The old code also looked itself up with GameObject.Find, which
            // has the same blind spot and would have minted a duplicate.)
            go.hideFlags = HideFlags.HideInHierarchy;
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<JoryioCallbackReceiver>();
        }

        internal static Action<SdkAuthError> OnSdkAuthErrorHandler;

        // Back in the foreground - after an iOS launch before the device's
        // first unlock, the first moment the saved held calls can be read.
        // They are sent then, and the calls made since after them, in order
        // (rules 68 and 93).
        private void OnApplicationPause(bool paused)
        {
            if (!paused) JoryioSDK.RetrySavedHeldCalls();
        }

        private void OnApplicationFocus(bool focused)
        {
            if (focused) JoryioSDK.RetrySavedHeldCalls();
        }

        // Invoked by name from native. Renaming it breaks the callback with no
        // compile error on either side - see the note in the bridges.
        //
        // [Preserve] AS WELL AS link.xml. Both guard against IL2CPP stripping a
        // method nothing in managed code calls, and they cover each other's
        // blind spot: the attribute travels with the method, while link.xml's
        // preserve="all" also covers members someone adds later and forgets to
        // attribute.
        //
        // MEASURED, not assumed. An iOS export on Unity 6000.4 with IL2CPP and
        // ManagedStrippingLevel.High generates these method bodies WITH BOTH
        // PROTECTIONS REMOVED - Unity is conservative about MonoBehaviour
        // methods, which SendMessage can invoke by name. So on this
        // configuration neither is currently load-bearing.
        //
        // Kept anyway, because they cost nothing and the stripper's behaviour
        // is not a contract: a higher stripping level, a future Unity version,
        // or moving these callbacks off a MonoBehaviour would each remove the
        // thing that is protecting them today. Re-run the export before
        // deleting either.
        [Preserve]
        private void OnInAppMessage(string json)
        {
            var handler = OnInAppMessageHandler;
            if (handler == null) return;

            JoryioInAppMessage message;
            try
            {
                var payload = JsonUtility.FromJson<InAppPayload>(json);
                if (!IsRenderableKind(payload.kind))
                {
                    // Neither native nor HTML content: there is nothing to
                    // draw, and handing it over made the game report a display
                    // of an empty panel (scan 5, N8).
                    Debug.LogWarning($"[Joryio] In-app message {payload.id} has no content this SDK can hand over (kind '{payload.kind}'); not passed to the handler.");
                    return;
                }
                var buttons = new JoryioInAppButton[payload.buttons == null ? 0 : payload.buttons.Length];
                for (var i = 0; i < buttons.Length; i++)
                {
                    var b = payload.buttons[i];
                    buttons[i] = new JoryioInAppButton(b.id, b.text, b.action, Blank(b.url));
                }
                message = new JoryioInAppMessage(
                    payload.id,
                    payload.name,
                    NormalizeType(payload.type),
                    payload.kind,
                    Blank(payload.title),
                    payload.body,
                    Blank(payload.imageUrl),
                    payload.closeButton,
                    payload.backdropDismissible,
                    buttons,
                    ToStyle(payload.style),
                    Blank(payload.html),
                    Blank(payload.css));
            }
            catch (Exception e)
            {
                // A malformed payload must not take down the callback path for
                // every subsequent message.
                Debug.LogError($"[Joryio] Could not parse in-app message payload: {e.Message}");
                return;
            }

            // The game's handler runs outside the parse block: an exception it
            // throws used to be logged as "Could not parse in-app message
            // payload", pointing the developer at the SDK instead of their own
            // renderer.
            try { handler(message); }
            catch (Exception e) { Debug.LogError($"[Joryio] In-app message handler threw: {e}"); }
        }

        // Invoked by name from native - see OnInAppMessage for why this carries
        // both [Preserve] and a link.xml entry.
        [Preserve]
        private void OnNativeResult(string json)
        {
            try
            {
                var payload = JsonUtility.FromJson<ResultPayload>(json);
                Resolve(payload.id, payload.value);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Joryio] Could not parse native result: {e.Message}");
            }
        }

        // Invoked by name from native when an ingest request is rejected under
        // SDK authentication - see OnInAppMessage for the [Preserve] reasoning.
        [Preserve]
        private void OnSdkAuthError(string json)
        {
            var handler = OnSdkAuthErrorHandler;
            if (handler == null) return;
            SdkAuthError error;
            try { error = JsonUtility.FromJson<SdkAuthError>(json); }
            catch (Exception e)
            {
                Debug.LogError($"[Joryio] Could not parse SDK authentication error: {e.Message}");
                return;
            }
            if (error == null) return;
            error.rawReason = Blank(error.rawReason);
            error.endpoint = Blank(error.endpoint);
            try { handler(error); }
            catch (Exception e) { Debug.LogError($"[Joryio] SDK authentication error handler threw: {e}"); }
        }

        // Only ever populated by JsonUtility, so every field here is unreferenced
        // from managed code and strippable for exactly the same reason.
        [Preserve]
        [Serializable]
        private struct ResultPayload
        {
            public int id;
            public string value;
        }

        // JsonUtility reads an absent string as null and an absent bool as
        // false; a native message that omits its optional fields therefore
        // arrives with them null, which is what the public model promises.
        // Field names are the bridge's JSON keys - and the React Native bridge's,
        // byte for byte - so renaming one here silently drops that field.
        [Preserve]
        [Serializable]
        private struct InAppPayload
        {
            public string id;
            public string name;
            public string type;
            public string kind;
            public string title;
            public string body;
            public string imageUrl;
            public bool closeButton;
            public bool backdropDismissible;
            public InAppButtonPayload[] buttons;
            public InAppStylePayload style;
            public string html;
            public string css;
        }

        // A CLASS with initialisers, not a struct. JsonUtility cannot express
        // "absent" for a number - it reads a missing key as 0 - and 0 is a real
        // corner radius and an unusable font size. The serializer builds this
        // through its default constructor, so a NaN initialiser survives when
        // the key is missing and is overwritten when it is present; ToStyle
        // turns NaN back into null, which is what the public model promises.
        [Preserve]
        [Serializable]
        private class InAppStylePayload
        {
            public string backgroundColor;
            public string textColor;
            public string primaryButtonColor;
            public string primaryButtonTextColor;
            public float cornerRadius = float.NaN;
            public float fontSize = float.NaN;
            public string titleWeight;
            public string textAlign;
            public string fontFamily;
        }

        /// <summary>
        /// Null when the campaign set no overrides. The serializer hands back a
        /// default instance whether or not the bridge sent a `style` key, so
        /// "absent" is detected from the fields rather than the reference.
        /// </summary>
        private static JoryioInAppStyle ToStyle(InAppStylePayload p)
        {
            if (p == null) return null;
            float? cornerRadius = float.IsNaN(p.cornerRadius) ? (float?)null : p.cornerRadius;
            float? fontSize = float.IsNaN(p.fontSize) ? (float?)null : p.fontSize;
            var backgroundColor = Blank(p.backgroundColor);
            var textColor = Blank(p.textColor);
            var primaryButtonColor = Blank(p.primaryButtonColor);
            var primaryButtonTextColor = Blank(p.primaryButtonTextColor);
            var titleWeight = Blank(p.titleWeight);
            var textAlign = Blank(p.textAlign);
            var fontFamily = Blank(p.fontFamily);
            if (backgroundColor == null && textColor == null && primaryButtonColor == null
                && primaryButtonTextColor == null && cornerRadius == null && fontSize == null
                && titleWeight == null && textAlign == null && fontFamily == null)
            {
                return null;
            }
            return new JoryioInAppStyle(
                backgroundColor, textColor, primaryButtonColor, primaryButtonTextColor,
                cornerRadius, fontSize, titleWeight, textAlign, fontFamily);
        }

        [Preserve]
        [Serializable]
        private struct InAppButtonPayload
        {
            public string id;
            public string text;
            public string action;
            public string url;
        }

        /// <summary>The presentation types the SDKs know, as the server spells them.</summary>
        private static readonly string[] KnownTypes = { "modal", "banner", "slideup", "fullscreen", "custom" };

        /// <summary>
        /// A presentation type the SDK does not know - a newer server value,
        /// or none - is "custom": the game decides placement. The iOS SDK
        /// decodes unknown values the same way; the Android bridge used to
        /// throw on them and send a message with no content at all.
        /// </summary>
        internal static string NormalizeType(string type) =>
            Array.IndexOf(KnownTypes, type) >= 0 ? type : "custom";

        /// <summary>Only "native" and "html" carry something to draw.</summary>
        internal static bool IsRenderableKind(string kind) => kind == "native" || kind == "html";

        /// <summary>
        /// JsonUtility may hand back "" rather than null for a string it did
        /// not see, depending on the Unity version; the public model promises
        /// null for "absent", so normalise here.
        /// </summary>
        private static string Blank(string value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
