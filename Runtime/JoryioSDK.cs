using System;
using System.Collections.Generic;
using UnityEngine;
using Joryio.Runtime.Platform;

namespace Joryio.Runtime
{
    /// <summary>
    /// The Joryio Unity SDK.
    ///
    /// Deliberately thin. Every method forwards to the native iOS or Android SDK
    /// and implements no tracking logic of its own - no queue, no session, no
    /// retry. Those exist four times already (web, iOS, Android, and the React
    /// Native bridge that also forwards), and every copy has drifted from the
    /// others at some point. A fifth would drift too.
    ///
    /// The method names match the native SDKs one for one, on purpose: the same
    /// parity rule that iOS and Android are held to by a tripwire, rather than
    /// a Unity-shaped API that has to be translated in someone's head.
    ///
    /// NOTHING HERE THROWS INTO THE GAME. Calls before Initialize, null
    /// arguments and native failures are logged and dropped: an analytics SDK
    /// that can take down a frame - or, on Android, surface an
    /// AndroidJavaException from a null string - is one a studio rips out.
    /// </summary>
    public static partial class JoryioSDK
    {
        /// <summary>
        /// Pinned to every other SDK by sdk-version-parity.spec.ts. It is a
        /// CONSTANT rather than something read from package.json because the
        /// tripwire compares source text across five languages, and because the
        /// Android SDK once logged a hardcoded "v1.0.0" while it was on 1.1.0 -
        /// a second copy is how that happens.
        /// </summary>
        public const string SdkVersion = "1.3.2";

        private static IJoryioNative _native;
        private static bool _initialized;
        private static JoryioEcommerce _ecommerce;

        private static IJoryioNative Native
        {
            get
            {
                if (_native != null) return _native;
#if UNITY_IOS && !UNITY_EDITOR
                _native = new JoryioIOS();
#elif UNITY_ANDROID && !UNITY_EDITOR
                _native = new JoryioAndroid();
#else
                _native = new JoryioStub();
#endif
                return _native;
            }
        }

        /// <summary>
        /// The SDK key prefix this platform's keys carry (rule 60). Keys are
        /// minted jry_sdk_&lt;platform&gt;_..., so an iOS or web key pasted into
        /// an Android build is refused here rather than filing every Android
        /// player, event and push token under the iOS app (scan 5, S2). Android
        /// used to accept any jry_sdk_ key. Null where there is no native SDK
        /// (Editor, desktop). Settable for tests.
        /// </summary>
        internal static string SdkKeyPrefix =
#if UNITY_IOS && !UNITY_EDITOR
            IosKeyPrefix;
#elif UNITY_ANDROID && !UNITY_EDITOR
            AndroidKeyPrefix;
#else
            null;
#endif

        internal const string IosKeyPrefix = "jry_sdk_ios_";
        internal const string AndroidKeyPrefix = "jry_sdk_android_";

        /// <summary>Replace the platform layer. Tests only.</summary>
        internal static void UseNative(IJoryioNative native, bool initialized)
        {
            _native = native;
            _initialized = initialized;
            _optOutBeforeInit = null;
            _consentBeforeInit = null;
            _lastConsent = null;
            _quietStartConsent = null;
            lock (HeldLock)
            {
                ResetHeldState();
                HeldStore = new MemoryHeldCallStore();
            }
        }

        /// <summary>Whether Initialize has succeeded. Calls before it are dropped with a warning.</summary>
        public static bool IsInitialized => _initialized;

        /// <summary>
        /// Start the SDK. IsInitialized stays false only for a missing key or
        /// host, or a key for another platform (wrong prefix); an iOS launch
        /// before the device's first unlock since boot is a success whose
        /// native start finishes at the unlock, with calls queued meanwhile.
        /// </summary>
        /// <param name="sdkKey">Your app's SDK key - a different one per platform.</param>
        /// <param name="apiHost">The API host shown for your workspace, e.g. https://api-eu1.joryio.com</param>
        /// <param name="config">Optional settings; see JoryioConfig.</param>
        public static void Initialize(string sdkKey, string apiHost, JoryioConfig config = null)
        {
            if (_initialized)
            {
                // Matches both natives, which also refuse re-initialisation: the
                // queue, session and timers are built once, and swapping the
                // host underneath them leaves half the SDK talking to the old one.
                Debug.LogWarning("[Joryio] Already initialized; ignoring.");
                return;
            }
            if (string.IsNullOrEmpty(sdkKey) || string.IsNullOrEmpty(apiHost))
            {
                Debug.LogError("[Joryio] Initialize needs an SDK key and an API host; the SDK stays off.");
                return;
            }
            // The one refusal Initialize reports (contract 47): a key the native
            // SDK will refuse. Both natives refuse it by logging, not throwing,
            // so without this check the game was told the SDK was running while
            // it sent nothing - and a second Initialize with the right key was
            // ignored as "already initialized".
            //
            // Anything else is success, including an iOS launch before the
            // device's first unlock since boot: the native SDK defers its start
            // until the unlock and queues calls meanwhile. That is not a failure
            // and is not reported as one.
            if (SdkKeyPrefix != null && !sdkKey.StartsWith(SdkKeyPrefix, StringComparison.Ordinal))
            {
                Debug.LogError($"[Joryio] Initialize: SDK keys for this platform start with '{SdkKeyPrefix}'; the SDK stays off. Copy the key from the Joryio dashboard.");
                return;
            }

            try
            {
                JoryioCallbackReceiver.Ensure();
                Native.SetCallbackTarget(JoryioCallbackReceiver.ObjectName);
                // Starts the native SDK and sends what was held before
                // Initialize, in order (JoryioHeldCalls.cs).
                StartNative(sdkKey, apiHost, config);
            }
            catch (Exception e)
            {
                // Left uninitialised, so every later call is a logged no-op
                // instead of a second exception from a half-built native SDK.
                Debug.LogError($"[Joryio] Initialize failed; the SDK stays off: {e}");
            }
        }

        private static bool Ready(string method)
        {
            if (_initialized) return true;
            Debug.LogWarning($"[Joryio] {method} called before Initialize; ignoring.");
            return false;
        }

        /// <summary>
        /// Ready, and every listed string argument present. A null crossing to
        /// Android throws AndroidJavaException (JNI cannot type a null), and
        /// on iOS it becomes "" - an event with no name, or an attribute with
        /// no key, that the server rejects after it has cost a request.
        /// </summary>
        private static bool Ready(string method, params string[] required)
        {
            if (!Ready(method)) return false;
            foreach (var arg in required)
            {
                if (string.IsNullOrEmpty(arg))
                {
                    Debug.LogWarning($"[Joryio] {method} called with a null or empty argument; ignoring.");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Run a native call; a native failure is logged, never thrown into
        /// the game. While the start is deferred until the saved calls can be
        /// read (rule 93, JoryioHeldCalls.cs) the call is kept, in order,
        /// behind them instead.
        /// </summary>
        /// <param name="keep">A withdrawal: the last to go if too many calls wait (rule 51).</param>
        /// <param name="onDropped">Settles the caller's callback if the waiting call is dropped.</param>
        private static void Call(string method, Action call, bool keep = false, Action onDropped = null)
        {
            if (DeferIfStartPending(method, call, keep, onDropped)) return;
            Invoke(method, call);
        }

        /// <summary>Run a native call now, whatever is waiting; a failure is logged.</summary>
        private static void Invoke(string method, Action call)
        {
            try { call(); }
            catch (Exception e) { Debug.LogError($"[Joryio] {method} failed: {e.Message}"); }
        }

        private static T Read<T>(string method, Func<T> read, T fallback)
        {
            if (!_initialized) return fallback;
            try { return read(); }
            catch (Exception e)
            {
                Debug.LogError($"[Joryio] {method} failed: {e.Message}");
                return fallback;
            }
        }

        /// <summary>
        /// Wrap a caller's callback so it runs at most once and never throws
        /// back into the SDK. The request-id table already drops a second
        /// native answer; this also covers the paths that answer from C#.
        /// </summary>
        private static Action<T> Once<T>(Action<T> callback)
        {
            if (callback == null) return _ => { };
            var settled = false;
            return value =>
            {
                if (settled) return;
                settled = true;
                try { callback(value); }
                catch (Exception e) { Debug.LogError($"[Joryio] Callback threw: {e}"); }
            };
        }

        // ── Analytics ────────────────────────────────────────────────────────

        public static void Track(string eventName, Dictionary<string, object> properties = null)
        {
            if (!Ready(nameof(Track), eventName)) return;
            // Serialized now: a call kept behind a deferred start (rule 93)
            // must not pick up later changes to the game's dictionary.
            var json = JoryioJson.Serialize(properties);
            Call(nameof(Track), () => Native.Track(eventName, json));
        }

        public static void TrackScreen(string screenName, Dictionary<string, object> properties = null)
        {
            if (!Ready(nameof(TrackScreen), screenName)) return;
            var json = JoryioJson.Serialize(properties);
            Call(nameof(TrackScreen), () => Native.TrackScreen(screenName, json));
        }

        /// <summary>
        /// Identify the player. While the player is opted out or consent is
        /// Denied, the id is only remembered on the device - nothing is sent or
        /// tracked - so that an unsubscribe made meanwhile reaches the right
        /// player; the normal identify runs once collection is allowed again.
        /// Reset and WipeData forget it.
        /// </summary>
        public static void Identify(string userId)
        {
            if (!Ready(nameof(Identify), userId)) return;
            // Saved calls go out first, under the identity they were made
            // for (rule 93: while they cannot be read, this waits behind them).
            RetrySavedHeldCalls();
            Call(nameof(Identify), () => Native.Identify(userId));
        }

        /// <summary>
        /// Log the player out: a NEW anonymous id, no user id, and an existing
        /// push token re-registered under the new identity.
        /// </summary>
        public static void Reset()
        {
            if (!Ready(nameof(Reset))) return;
            RetrySavedHeldCalls();
            Call(nameof(Reset), () => Native.Reset());
        }

        public static void Flush()
        {
            if (!Ready(nameof(Flush))) return;
            Call(nameof(Flush), () => Native.Flush());
        }

        public static string GetUserId() => Read(nameof(GetUserId), () => Native.GetUserId() ?? string.Empty, string.Empty);
        public static string GetAnonymousId() => Read(nameof(GetAnonymousId), () => Native.GetAnonymousId() ?? string.Empty, string.Empty);

        /// <summary>
        /// The identifiers this device is currently reporting under. userId is
        /// null for an anonymous player. Same pair as the natives' getIdentity.
        /// </summary>
        public static (string userId, string anonymousId) GetIdentity()
        {
            var userId = GetUserId();
            return (string.IsNullOrEmpty(userId) ? null : userId, GetAnonymousId());
        }

        // ── Identity and attributes ──────────────────────────────────────────

        /// <summary>
        /// Attach another identifier to this player - a labelled alias such as
        /// ("steam_id", "7656119...") - so events and profiles from that system
        /// resolve to the same person. Works for an anonymous player too; there
        /// is no need to Identify first.
        /// </summary>
        /// <param name="label">What kind of identifier it is, e.g. "steam_id".</param>
        /// <param name="name">The identifier itself.</param>
        public static void AddAlias(string label, string name)
        {
            if (!Ready(nameof(AddAlias), label, name)) return;
            Call(nameof(AddAlias), () => Native.AddAlias(label, name));
        }

        /// <summary>
        /// Set several attributes at once. A null value UNSETS that attribute,
        /// the same as UnsetAttribute(key) (SDK contract 19) - on iOS and
        /// Android alike.
        /// </summary>
        public static void SetAttributes(Dictionary<string, object> attributes)
        {
            if (!Ready(nameof(SetAttributes))) return;
            if (attributes == null || attributes.Count == 0) return;

            // Nulls are split out here rather than left to the bridges, which
            // used to disagree: Android dropped a null silently and iOS sent it
            // as a value. Doing it once, above both, is the only place it can
            // stay identical.
            var values = new Dictionary<string, object>();
            var unset = new List<string>();
            foreach (var pair in attributes)
            {
                if (string.IsNullOrEmpty(pair.Key)) continue;
                if (pair.Value == null) unset.Add(pair.Key);
                else if (NotANumber(nameof(SetAttributes), pair.Key, pair.Value)) continue;
                else values[pair.Key] = pair.Value;
            }
            if (values.Count > 0)
            {
                var json = JoryioJson.Serialize(values);
                Call(nameof(SetAttributes), () => Native.SetAttributes(json));
            }
            foreach (var key in unset)
            {
                Call(nameof(SetAttributes), () => Native.UnsetAttribute(key));
            }
        }

        /// <summary>
        /// Set one attribute. The value keeps its type across the boundary:
        /// SetAttribute("level", 42) stores a number, not "42". Segments compare
        /// numerically, so a stringified number silently matches nobody.
        ///
        /// A null value UNSETS the attribute, exactly like UnsetAttribute(key)
        /// (SDK contract 19). It used to differ by platform: Android dropped
        /// the call and iOS stored a null.
        /// </summary>
        public static void SetAttribute(string key, object value)
        {
            if (!Ready(nameof(SetAttribute), key)) return;
            if (value == null)
            {
                Call(nameof(SetAttribute), () => Native.UnsetAttribute(key));
                return;
            }
            if (NotANumber(nameof(SetAttribute), key, value)) return;
            var json = JoryioJson.Serialize(new Dictionary<string, object> { { "v", value } });
            Call(nameof(SetAttribute), () => Native.SetAttribute(key, json));
        }

        public static void IncrementAttribute(string key, double by = 1)
        {
            if (!Ready(nameof(IncrementAttribute), key)) return;
            if (NotANumber(nameof(IncrementAttribute), key, by)) return;
            Call(nameof(IncrementAttribute), () => Native.IncrementAttribute(key, by));
        }

        /// <summary>
        /// A float or double that is NaN or infinite - a value with no JSON
        /// spelling. As an attribute value it is IGNORED with a warning, not
        /// written: the JSON writer turns it into null, and a null attribute
        /// value means "unset", so a division by zero in the game used to
        /// DELETE the player's attribute (scan 3, Unity 9). Inside an event's
        /// properties, or nested in a list, it still becomes null - there it
        /// is one unknown value, not a deletion.
        /// </summary>
        private static bool NotANumber(string method, string key, object value)
        {
            var bad = (value is double d && (double.IsNaN(d) || double.IsInfinity(d)))
                || (value is float f && (float.IsNaN(f) || float.IsInfinity(f)));
            if (bad) Debug.LogWarning($"[Joryio] {method}: '{key}' is NaN or infinite; ignoring it (the attribute is left unchanged).");
            return bad;
        }

        public static void UnsetAttribute(string key)
        {
            if (!Ready(nameof(UnsetAttribute), key)) return;
            Call(nameof(UnsetAttribute), () => Native.UnsetAttribute(key));
        }

        // ── Consent ──────────────────────────────────────────────────────────

        // Consent and opt-out are the three calls that work BEFORE Initialize:
        // a consent screen often answers before the SDK starts. Both native
        // SDKs hold the decision and apply it inside initialization, so the
        // first Session Start, sync or push registration never goes out ahead
        // of it (SDK contracts 4, 5 and 25).
        //
        // They are TWO switches (contract 24, consent model A): OptOut/OptIn
        // is the player's own choice, UpdateConsent is your consent screen's
        // answer. Data is collected only when consent is granted AND the
        // player has not opted out; either "no" wins. Neither call changes the
        // other switch.

        /// <summary>
        /// The opt-out made before Initialize in THIS launch, if any. It is the
        /// latest, explicit decision, so it wins over the stored one the native
        /// getter reports; and if the native call fails, a game that called
        /// OptOut() and then asked still hears "true" (scan 3, Unity 5).
        /// </summary>
        private static bool? _optOutBeforeInit;

        /// <summary>
        /// Resume collecting after OptOut. The only call that lifts an
        /// opt-out: granting consent does not. Works before Initialize too.
        /// </summary>
        public static void OptIn()
        {
            if (!_initialized) _optOutBeforeInit = false;
            else NoteOptOutWhileDeferred(false);
            Call(nameof(OptIn), () => Native.OptIn());
        }

        /// <summary>
        /// Stop collecting. Queued events and pending attribute writes are
        /// discarded, not held, and the choice survives a restart. Does not
        /// change the tracking consent. Works before Initialize too; to start a
        /// session already opted out you can also set JoryioConfig.OptOut.
        /// </summary>
        public static void OptOut()
        {
            if (!_initialized)
            {
                _optOutBeforeInit = true;
                // A held subscribe collects; a held unsubscribe stays (contract 36).
                DiscardHeldNonWithdrawals();
            }
            else NoteOptOutWhileDeferred(true);
            Call(nameof(OptOut), () => Native.OptOut());
        }

        /// <summary>
        /// Whether the player has opted out (OptOut/OptIn only - a denied
        /// consent is a separate switch and does not show here).
        ///
        /// Safe before Initialize: an OptOut or OptIn made earlier in this
        /// launch, otherwise the opt-out the native SDK has stored from an
        /// earlier launch (contract 44 - both natives read their storage for
        /// this before they start).
        /// </summary>
        public static bool IsUserOptedOut()
        {
            if (!_initialized)
            {
                // The native answer is the stored decision; this launch's own
                // call, being later and explicit, wins where there is one.
                bool native;
                try { native = Native.IsUserOptedOut(); }
                catch (Exception) { native = false; }
                return _optOutBeforeInit ?? native;
            }
            // An OptOut or OptIn still waiting behind a deferred start (rule
            // 93) is the latest decision.
            return OptOutWhileDeferred() ?? Read(nameof(IsUserOptedOut), () => Native.IsUserOptedOut(), false);
        }

        /// <summary>
        /// Record the player's answer to your consent screen. Granted releases
        /// anything queued while consent was Pending; Denied drops it, collects
        /// nothing further and is remembered across restarts until Granted.
        /// Pending holds events for this launch only. Start a session with
        /// JoryioConfig.TrackingConsent = Pending to hold events from launch.
        ///
        /// Never touches the opt-out: Granted does not lift an OptOut, and
        /// Denied does not record one. A value outside the enum is ignored with
        /// a warning - it used to be sent as "granted".
        /// </summary>
        public static void UpdateConsent(TrackingConsent consent)
        {
            var wire = consent.ToWire();
            if (wire == null)
            {
                Debug.LogWarning($"[Joryio] UpdateConsent called with an unknown value ({(int)consent}); ignoring.");
                return;
            }
            _lastConsent = consent;
            if (!_initialized)
            {
                _consentBeforeInit = consent;
                // Newer than the consent a failed quiet start computed: the
                // retry hands back this one (scan 6, N2).
                _quietStartConsent = null;
                if (consent == TrackingConsent.Denied) DiscardHeldNonWithdrawals();
            }
            else NoteConsentWhileDeferred(consent);
            Call(nameof(UpdateConsent), () => Native.UpdateConsent(wire));
        }

        /// <summary>
        /// The UpdateConsent made before Initialize in THIS launch, if any -
        /// the consent a quiet start hands back (see StartNative).
        /// </summary>
        private static TrackingConsent? _consentBeforeInit;

        /// <summary>
        /// The last consent this game set in this launch - by UpdateConsent or
        /// an explicit JoryioConfig.TrackingConsent. Used only when the native
        /// SDK gives no answer: the Editor stub, or a native call that failed.
        /// Never in place of a native answer - the native knows a Denied
        /// stored by an earlier launch, which this launch's Pending does not
        /// reopen (contract 45).
        /// </summary>
        private static TrackingConsent? _lastConsent;

        /// <summary>
        /// The current tracking consent: Granted, Denied or Pending - the
        /// consent switch only; the opt-out is IsUserOptedOut(). The same
        /// getter as the native SDKs' getTrackingConsent().
        ///
        /// Safe before Initialize, and answered from the native SDK's stored
        /// decision then too, so a Denied remembered from an earlier launch is
        /// reported as Denied, not as a default Granted (contract 44). Granted
        /// only when nothing has been decided anywhere - which is also how the
        /// SDK behaves then (consent model A: unset and nothing stored tracks).
        /// </summary>
        public static TrackingConsent GetTrackingConsent()
        {
            // A deferred start (rule 93) holds the native SDK at Pending until
            // the saved calls can be read; the game's consent is the one
            // waiting to be handed back, or a newer UpdateConsent behind it.
            var deferred = ConsentWhileDeferred();
            if (deferred.HasValue) return deferred.Value;
            string wire = null;
            try { wire = Native.GetTrackingConsent(); }
            catch (Exception e) { Debug.LogError($"[Joryio] GetTrackingConsent failed: {e.Message}"); }
            // The native answer always wins, before Initialize as after it.
            var parsed = TrackingConsentWire.FromWire(wire);
            return parsed ?? _lastConsent ?? TrackingConsent.Granted;
        }

        /// <summary>An explicit, valid JoryioConfig.TrackingConsent becomes the last known consent.</summary>
        internal static void RememberConfiguredConsent(JoryioConfig config)
        {
            var configured = config?.TrackingConsent;
            if (configured.HasValue && configured.Value.ToWire() != null) _lastConsent = configured.Value;
        }

        // ── Marketing subscription ───────────────────────────────────────────
        //
        // A DIFFERENT consent from OptIn/OptOut above: that is whether we may
        // OBSERVE this person, this is whether we may MESSAGE them. Existed on
        // web only, so no mobile or game client could offer a preference centre.

        /// <summary>Channels the backend accepts. Five, including viber.</summary>
        private static readonly string[] SubscriptionChannels =
            { "email", "sms", "whatsapp", "push", "viber" };

        /// <summary>Wire values are camelCase; the API rejects `opted_in`.</summary>
        private static readonly string[] SubscriptionStatuses =
            { "optedIn", "subscribed", "unsubscribed" };

        /// <summary>
        /// Set the marketing subscription status for one channel.
        ///
        /// Made before Initialize, it is held (and kept across a restart) and
        /// sent when the SDK starts; a newer change of the same channel
        /// replaces an older held one, so the player's last choice is the one
        /// sent. An OptOut, a Denied consent or a WipeData before Initialize
        /// discards a held subscribe, never a held unsubscribe. A subscribe
        /// made before Initialize while the player is opted out or consent is
        /// Denied is dropped, not held: a later OptIn or Granted does not send
        /// it (rule 73).
        /// </summary>
        public static void SetSubscription(string channel, string status)
        {
            if (!ValidChannel(channel)) return;
            if (Array.IndexOf(SubscriptionStatuses, status) < 0)
            {
                Debug.LogError($"[Joryio] Invalid status: {status}. Must be one of: {string.Join(", ", SubscriptionStatuses)}");
                return;
            }
            if (TryHold(new HeldCall(HeldCall.Channel, channel, status, null))) return;
            Call(nameof(SetSubscription), () => Native.SetSubscription(channel, status), keep: status == "unsubscribed");
        }

        /// <summary>
        /// Join a subscription group (list) on a channel. Before Initialize it
        /// is held like SetSubscription.
        /// </summary>
        public static void AddToSubscriptionGroup(string groupId, string channel)
        {
            if (!ValidGroup(groupId) || !ValidChannel(channel)) return;
            if (TryHold(new HeldCall(HeldCall.Group, channel, "subscribe", groupId))) return;
            Call(nameof(AddToSubscriptionGroup), () => Native.AddToSubscriptionGroup(groupId, channel));
        }

        /// <summary>
        /// Leave a subscription group (list) on a channel. Made before
        /// Initialize, it is held and sent when the SDK starts, whatever
        /// happens in between.
        /// </summary>
        public static void RemoveFromSubscriptionGroup(string groupId, string channel)
        {
            if (!ValidGroup(groupId) || !ValidChannel(channel)) return;
            if (TryHold(new HeldCall(HeldCall.Group, channel, "unsubscribe", groupId))) return;
            Call(nameof(RemoveFromSubscriptionGroup), () => Native.RemoveFromSubscriptionGroup(groupId, channel), keep: true);
        }

        private static bool ValidChannel(string channel)
        {
            if (channel != null && Array.IndexOf(SubscriptionChannels, channel) >= 0) return true;
            Debug.LogError($"[Joryio] Invalid channel: {channel ?? "null"}. Must be one of: {string.Join(", ", SubscriptionChannels)}");
            return false;
        }

        private static bool ValidGroup(string groupId)
        {
            if (!string.IsNullOrWhiteSpace(groupId)) return true;
            Debug.LogError("[Joryio] Cannot update subscription group: groupId is required");
            return false;
        }

        /// <summary>
        /// Erase everything stored on the device for this profile. The opt-out
        /// is kept, and so are unsubscribes not yet delivered.
        ///
        /// Works before Initialize too: the native SDK records it and erases
        /// inside its own start-up, before anything is sent (contract 25). If
        /// withdrawals made before it are held, the wipe is held behind them,
        /// so they still reach the player who asked.
        /// </summary>
        public static void WipeData()
        {
            if (HoldWipeIfNeeded()) return;
            RetrySavedHeldCalls();
            Call(nameof(WipeData), () => Native.WipeData());
        }

        // ── Push ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Hand us a token you obtained yourself - the APNs device token (hex)
        /// on iOS, the FCM registration token on Android.
        ///
        /// The path to use when the game already has a push stack: Firebase
        /// Messaging, Unity's Mobile Notifications package, or a studio's own
        /// wrapper. On Android the SDK then never needs its own FCM service.
        /// On iOS, note that Initialize still makes the SDK the notification
        /// center's delegate (forwarding to the delegate that was there
        /// before), and receives the APNs token from Unity's app controller by
        /// itself - see the README section on existing push stacks.
        ///
        /// Calling it is "push on" for this device: it lifts an earlier
        /// UnregisterPush. So do not forward a token your push stack delivers
        /// by itself (Firebase's OnTokenReceived fires at every start) while
        /// the player has turned push off - only when they turn it back on.
        /// Before Initialize it is not sent, and it does not cancel a held
        /// UnregisterPush.
        /// </summary>
        public static void RegisterPushToken(string token) => RegisterPushToken(token, null);

        /// <summary>
        /// Same, and hear how it went. Before this the outcome was dropped at
        /// the bridge: a transient failure (offline, DNS) left the device
        /// unregistered for push with nothing to retry. <c>isRetryable</c>
        /// says trying again is worth it; <c>isUnauthorized</c> says the SDK
        /// key is wrong. The callback runs on the main thread, exactly once.
        /// </summary>
        public static void RegisterPushToken(string token, Action<PushRegistrationResult> onResult)
        {
            var settle = Once(onResult);
            // Before Initialize this does NOT cancel a held UnregisterPush
            // (rule 69): Firebase's OnTokenReceived fires by itself at every
            // start, and a game that forwards it here used to undo the
            // player's "push off" without the player doing anything. The
            // held unregister wins; a RegisterPushToken after Initialize - the
            // player turning push back on - lifts it.
            if (!Ready(nameof(RegisterPushToken), token))
            {
                settle(new PushRegistrationResult
                {
                    success = false,
                    message = _initialized ? "No token" : "SDK not initialized",
                    isRetryable = !_initialized,
                });
                return;
            }
            // Saved calls go out first: a saved UnregisterPush is older than
            // this and must not land after it (rule 93: while they cannot be
            // read, this waits behind them).
            RetrySavedHeldCalls();
            Call(nameof(RegisterPushToken), () =>
            {
                var requestId = JoryioCallbackReceiver.Register(json =>
                {
                    PushRegistrationResult r;
                    try { r = JsonUtility.FromJson<PushRegistrationResult>(json); }
                    catch (Exception) { r = null; }
                    settle(r ?? new PushRegistrationResult { success = false, message = "Unreadable result", isRetryable = true });
                });
                try { Native.RegisterPushToken(token, requestId); }
                catch (Exception e)
                {
                    JoryioCallbackReceiver.Forget(requestId);
                    Debug.LogError($"[Joryio] {nameof(RegisterPushToken)} failed: {e.Message}");
                    settle(new PushRegistrationResult { success = false, message = e.Message, isRetryable = true });
                }
            }, onDropped: () => settle(new PushRegistrationResult { success = false, message = "Dropped: too many calls waiting", isRetryable = true }));
        }

        /// <summary>
        /// Report that a push ARRIVED on this device.
        /// </summary>
        /// <remarks>
        /// Needed because most games run their OWN push stack - Firebase
        /// Messaging, Unity Mobile Notifications, a studio wrapper - so a push
        /// never passes through our native service and would report no delivery
        /// at all. The same games already call RegisterPushToken; this is the
        /// matching half.
        ///
        /// Pass the `trackingId` from the message's data payload.
        /// </remarks>
        public static void ReportPushDelivered(string trackingId)
        {
            if (!Ready(nameof(ReportPushDelivered), trackingId)) return;
            Call(nameof(ReportPushDelivered), () => Native.ReportPushDelivered(trackingId));
        }

        /// <summary>
        /// Stop push to this device: the token is unregistered on the server
        /// and stays off across restarts until RegisterPushToken (after
        /// Initialize) or a permission request. A withdrawal (contract 36): made before
        /// Initialize it is held, kept across a restart, and sent when the SDK
        /// starts, whatever OptOut, consent or WipeData happen in between.
        /// </summary>
        public static void UnregisterPush()
        {
            if (TryHold(new HeldCall(HeldCall.Push, null, null, null))) return;
            Call(nameof(UnregisterPush), () => Native.UnregisterPush(), keep: true);
        }

        public static string GetDeviceToken() => Read(nameof(GetDeviceToken), () => Native.GetDeviceToken() ?? string.Empty, string.Empty);

        /// <summary>Report that a notification was opened.</summary>
        public static void TrackPushClick(string trackingId)
        {
            if (!Ready(nameof(TrackPushClick), trackingId)) return;
            Call(nameof(TrackPushClick), () => Native.TrackPushClick(trackingId));
        }

        /// <summary>
        /// Ask for the notification permission.
        ///
        /// Callback-shaped rather than returning a value because iOS answers
        /// asynchronously and Android does not; a game should not have to know
        /// which. The callback runs on the main thread, exactly once - with the
        /// player's answer once the system prompt closes, or at once when there
        /// is nothing to ask (already granted, already denied, Android below 13).
        ///
        /// Ask when the player has a reason to say yes. A prompt in the first
        /// seconds of a game is the one most often denied, and a denial is far
        /// harder to reverse than a delay.
        /// </summary>
        public static void RequestPushPermission(Action<bool> onResult)
        {
            var settle = Once(onResult);
            if (!Ready(nameof(RequestPushPermission))) { settle(false); return; }
            // Waits behind a deferred start (rule 93): a grant turns push on,
            // which a saved UnregisterPush must not land after.
            Call(nameof(RequestPushPermission),
                () => Ask(nameof(RequestPushPermission), id => Native.RequestPushPermission(id), v => settle(v == "true"), () => settle(false)),
                onDropped: () => settle(false));
        }

        /// <summary>
        /// granted, denied, notDetermined, or unavailable - the same words on
        /// iOS, Android and React Native. notDetermined is the only state in
        /// which the system prompt can still appear.
        /// </summary>
        public static void GetPushPermissionStatus(Action<string> onResult)
        {
            var settle = Once(onResult);
            if (!Ready(nameof(GetPushPermissionStatus))) { settle("unavailable"); return; }
            Ask(nameof(GetPushPermissionStatus), id => Native.GetPushPermissionStatus(id), settle, () => settle("unavailable"));
        }

        public static void IsPushEnabled(Action<bool> onResult)
        {
            var settle = Once(onResult);
            if (!Ready(nameof(IsPushEnabled))) { settle(false); return; }
            Ask(nameof(IsPushEnabled), id => Native.IsPushEnabled(id), v => settle(v == "true"), () => settle(false));
        }

        /// <summary>Register a pending answer, make the native call, and settle with the fallback if it throws. A read: never waits behind a deferred start.</summary>
        private static void Ask(string method, Action<int> call, Action<string> onAnswer, Action onFailure)
        {
            var requestId = JoryioCallbackReceiver.Register(onAnswer);
            try { call(requestId); }
            catch (Exception e)
            {
                JoryioCallbackReceiver.Forget(requestId);
                Debug.LogError($"[Joryio] {method} failed: {e.Message}");
                onFailure();
            }
        }

        /// <summary>
        /// iOS only: set the app icon's badge number. Android has no badge API
        /// of its own - launchers decide - so there it does nothing.
        /// </summary>
        public static void UpdateBadgeCount(int count)
        {
            if (!Ready(nameof(UpdateBadgeCount))) return;
            Call(nameof(UpdateBadgeCount), () => Native.UpdateBadgeCount(count));
        }

        /// <summary>iOS only: clear the app icon's badge. Does nothing on Android.</summary>
        public static void ClearBadge()
        {
            if (!Ready(nameof(ClearBadge))) return;
            Call(nameof(ClearBadge), () => Native.ClearBadge());
        }

        // ── Session and auth ─────────────────────────────────────────────────

        public static string GetSessionId() => Read(nameof(GetSessionId), () => Native.GetSessionId() ?? string.Empty, string.Empty);

        /// <summary>
        /// The current session: its id, when it started (UTC), and how long it
        /// has run, in seconds. Same three values as iOS's getSessionInfo.
        /// Empty id and DateTime.MinValue before Initialize.
        /// </summary>
        public static (string sessionId, DateTime sessionStart, double duration) GetSessionInfo()
        {
            var json = Read(nameof(GetSessionInfo), () => Native.GetSessionInfo(), string.Empty);
            if (string.IsNullOrEmpty(json)) return (string.Empty, DateTime.MinValue, 0);
            try
            {
                var p = JsonUtility.FromJson<SessionInfoPayload>(json);
                var start = DateTimeOffset.FromUnixTimeMilliseconds(p.sessionStartMs).UtcDateTime;
                return (p.sessionId ?? string.Empty, start, p.durationMs / 1000.0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Joryio] Could not read session info: {e.Message}");
                return (string.Empty, DateTime.MinValue, 0);
            }
        }

        [Serializable]
        private struct SessionInfoPayload
        {
            public string sessionId;
            public long sessionStartMs;
            public long durationMs;
        }

        /// <summary>
        /// Supply a signed token when SDK authentication is enabled for the
        /// workspace (JoryioConfig.EnableSdkAuthentication). Without it those
        /// requests are rejected.
        ///
        /// Safe before Initialize: the native SDK keeps it and starts with it,
        /// ahead of its first request, and keeps it through a WipeData made
        /// before Initialize - so an unsubscribe held for the player who is
        /// being erased still goes out with their token (rule 84). It used to
        /// be ignored before Initialize.
        /// </summary>
        public static void SetSdkAuthenticationToken(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                Debug.LogWarning($"[Joryio] {nameof(SetSdkAuthenticationToken)} called with a null or empty argument; ignoring.");
                return;
            }
            Call(nameof(SetSdkAuthenticationToken), () => Native.SetSdkAuthenticationToken(token));
        }

        /// <summary>
        /// Hear when the server rejects one of the CURRENT player's requests
        /// under SDK authentication - an expired or invalid token, or none set
        /// yet (reason missing). Mint a fresh token and call
        /// SetSdkAuthenticationToken; the current player's refused events and
        /// writes are kept and retried with it. Never called for the leftovers
        /// of a player who has logged out: those go out with that player's own
        /// token, and are dropped if the server refuses them, so the handler
        /// never makes you sign out the player who is playing now. Runs on the
        /// main thread. Pass null to stop listening. Safe before Initialize,
        /// like SetSdkAuthenticationToken.
        /// </summary>
        public static void SetSdkAuthenticationErrorHandler(Action<SdkAuthError> handler)
        {
            var first = JoryioCallbackReceiver.OnSdkAuthErrorHandler == null;
            JoryioCallbackReceiver.OnSdkAuthErrorHandler = handler;
            if (handler != null && first)
            {
                Call(nameof(SetSdkAuthenticationErrorHandler), () => Native.EnableSdkAuthenticationErrors());
            }
        }

        // ── E-commerce ───────────────────────────────────────────────────────

        /// <summary>
        /// E-commerce events with the platform's canonical names applied:
        /// <c>JoryioSDK.Ecommerce.Purchase(order)</c>. Prefer this to Track for
        /// commerce - a hand-typed event name matches no revenue report.
        /// </summary>
        public static JoryioEcommerce Ecommerce => _ecommerce ?? (_ecommerce = new JoryioEcommerce());

        internal static void SendEcommerce(string method, Dictionary<string, object> payload)
        {
            if (!Ready("Ecommerce." + method)) return;
            var json = JoryioJson.Serialize(payload);
            Call("Ecommerce." + method, () => Native.Ecommerce(method, json));
        }

        // ── In-app ───────────────────────────────────────────────────────────

        /// <summary>
        /// Render in-app messages yourself, in-engine, instead of letting the
        /// native SDK draw an OS-styled overlay over your game.
        ///
        /// Leave this unset and the native SDK renders. Set it and YOU render -
        /// and you must report what happened through the message object's
        /// MarkDisplayed / MarkClicked / MarkDismissed. That is why the callback
        /// hands you a JoryioInAppMessage rather than raw data: the SDK cannot
        /// see a message it did not draw, and a campaign that is never reported
        /// reads as delivered to nobody: no display, nothing for attribution to
        /// join on, and frequency caps that never count it.
        /// </summary>
        /// <param name="handler">
        /// Your renderer. Null is the same as ClearInAppMessageHandler: the
        /// native SDK draws again.
        /// </param>
        /// <param name="canRenderHtml">
        /// Whether your renderer can display HTML content, which needs a web
        /// view - a plain Unity canvas cannot. This is not cosmetic: the server
        /// targets campaigns on what the device reports, and it resolves an
        /// absent capability to "no" rather than "unknown". Claiming HTML you
        /// cannot draw sends campaigns to players who will never see them;
        /// omitting HTML you CAN draw withholds campaigns you could have shown.
        /// </param>
        public static void SetInAppMessageHandler(
            Action<JoryioInAppMessage> handler,
            bool canRenderHtml = false)
        {
            if (handler == null)
            {
                // A null handler used to be stored as-is while the native
                // callback stayed installed: every message was handed to C#,
                // dropped by the receiver, and never drawn by anyone.
                ClearInAppMessageHandler();
                return;
            }
            if (!Ready(nameof(SetInAppMessageHandler))) return;
            JoryioCallbackReceiver.OnInAppMessageHandler = handler;

            // Declared from the handler that is actually being installed, not
            // from a config flag describing the native SDK's own views - those
            // views stop running the moment this is set.
            var capabilities = canRenderHtml
                ? "[\"content.native\",\"content.html\"]"
                : "[\"content.native\"]";
            Call(nameof(SetInAppMessageHandler), () => Native.EnableInAppCallback(capabilities));
        }

        /// <summary>
        /// Stop rendering in-app messages yourself and hand them back to the
        /// native SDK, which draws them with its own views again and reports
        /// its own capabilities. The same as SetInAppMessageHandler(null), and
        /// the native SDKs' clearInAppMessageCallback.
        /// </summary>
        public static void ClearInAppMessageHandler()
        {
            JoryioCallbackReceiver.OnInAppMessageHandler = null;
            if (!Ready(nameof(ClearInAppMessageHandler))) return;
            Call(nameof(ClearInAppMessageHandler), () => Native.ClearInAppCallback());
        }

        /// <summary>
        /// Hand rendering back to the native SDK only if <paramref name="handler"/>
        /// is still the installed one. For a renderer that is going away (its
        /// OnDestroy) and must not remove a handler another script installed
        /// after it - the plain ClearInAppMessageHandler() would. The same
        /// shape as React Native's per-listener unsubscribe.
        /// </summary>
        /// <param name="handler">
        /// The delegate you passed to SetInAppMessageHandler. Null clears
        /// whatever is installed, the same as ClearInAppMessageHandler() and
        /// SetInAppMessageHandler(null) - it used to do nothing, so a null
        /// left the game rendering when it meant to stop.
        /// </param>
        public static void ClearInAppMessageHandler(Action<JoryioInAppMessage> handler)
        {
            if (handler != null && JoryioCallbackReceiver.OnInAppMessageHandler != handler) return;
            ClearInAppMessageHandler();
        }

        /// <summary>
        /// Report an in-app interaction. Prefer the methods on
        /// JoryioInAppMessage, which cannot be forgotten as easily.
        /// </summary>
        /// <param name="action">displayed, clicked or dismissed.</param>
        /// <param name="elementId">For a click, the id of the button or link tapped (sent as `elementId`, the same name on every SDK).</param>
        public static void TrackInAppImpression(string campaignId, string action, string elementId = null)
        {
            if (!Ready(nameof(TrackInAppImpression), campaignId, action)) return;
            Call(nameof(TrackInAppImpression), () => Native.TrackInAppImpression(campaignId, action, elementId));
        }

        /// <summary>
        /// Submit a form from an HTML in-app message your game renders itself
        /// (a <c>&lt;form data-jry-form&gt;</c> in the campaign's Html). Values
        /// are strings, or a list of strings for a checkbox group. They are
        /// posted with this player's identity; the server validates them
        /// against the campaign's form and writes the profile. The callback
        /// runs on the main thread, exactly once, and never sees an exception:
        /// a failure is ok = false.
        /// </summary>
        public static void SubmitInAppForm(
            string campaignId,
            Dictionary<string, object> values,
            Action<InAppFormSubmitResponse> onResult)
        {
            var settle = Once(onResult);
            var failed = new InAppFormSubmitResponse { ok = false, message = "Something went wrong. Please try again." };
            if (!Ready(nameof(SubmitInAppForm), campaignId)) { settle(failed); return; }
            var valuesJson = JoryioJson.Serialize(values);
            Call(nameof(SubmitInAppForm), () =>
            {
                var requestId = JoryioCallbackReceiver.Register(json =>
                {
                    InAppFormSubmitResponse r;
                    try { r = JsonUtility.FromJson<InAppFormSubmitResponse>(json); }
                    catch (Exception) { r = null; }
                    settle(r ?? failed);
                });
                try { Native.SubmitInAppForm(campaignId, valuesJson, requestId); }
                catch (Exception e)
                {
                    JoryioCallbackReceiver.Forget(requestId);
                    Debug.LogError($"[Joryio] {nameof(SubmitInAppForm)} failed: {e.Message}");
                    settle(failed);
                }
            }, onDropped: () => settle(failed));
        }

        /// <summary>Pull the latest campaigns now, rather than at the next session sync.</summary>
        public static void SyncInAppCampaigns()
        {
            if (!Ready(nameof(SyncInAppCampaigns))) return;
            Call(nameof(SyncInAppCampaigns), () => Native.SyncInAppCampaigns());
        }

        /// <summary>
        /// Re-run campaign targeting against what the SDK already holds, without
        /// going to the network - after changing attributes locally, say. A
        /// TESTING affordance: ignored unless JoryioConfig.Debug is on, because
        /// in production it can force a message on screen.
        /// </summary>
        public static void EvaluateInAppCampaigns()
        {
            if (!Ready(nameof(EvaluateInAppCampaigns))) return;
            Call(nameof(EvaluateInAppCampaigns), () => Native.EvaluateInAppCampaigns());
        }

        /// <summary>
        /// Forget which campaigns have already been shown. A TESTING affordance:
        /// frequency state is local, so without it the only way to re-test a
        /// campaign is to reinstall. Ignored unless JoryioConfig.Debug is on.
        /// </summary>
        public static void ResetDisplayedCampaigns()
        {
            if (!Ready(nameof(ResetDisplayedCampaigns))) return;
            Call(nameof(ResetDisplayedCampaigns), () => Native.ResetDisplayedCampaigns());
        }
    }
}
