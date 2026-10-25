using System.Collections.Generic;

namespace Joryio.Runtime
{
    /// <summary>
    /// Optional settings, mirroring the fields both native SDKs accept.
    ///
    /// Serialized to JSON and parsed on the native side rather than marshalled
    /// field by field, so adding a setting is a change in three files that
    /// cannot silently disagree about argument order.
    ///
    /// Durations are SECONDS here and carry the unit in their name, because the
    /// natives disagree: iOS takes seconds, Android milliseconds. The bridges
    /// convert.
    /// </summary>
    public sealed class JoryioConfig
    {
        /// <summary>
        /// Verbose native logging, on both platforms. Also unlocks the testing
        /// calls (ResetDisplayedCampaigns, EvaluateInAppCampaigns), which the
        /// native SDKs ignore without it. Off by default.
        /// </summary>
        public bool Debug { get; set; }

        /// <summary>Events per batch before an automatic flush.</summary>
        public int BatchSize { get; set; } = 50;

        /// <summary>Seconds between automatic flushes.</summary>
        public double FlushIntervalSeconds { get; set; } = 5;

        /// <summary>
        /// Send each event as soon as it is tracked instead of batching. Off
        /// by default, and not recommended for production - one request per
        /// event.
        /// </summary>
        public bool SendImmediately { get; set; }

        /// <summary>
        /// Most events the local queue holds while flushes fail; beyond it the
        /// oldest are dropped.
        /// </summary>
        public int MaxQueueSize { get; set; } = 1000;

        /// <summary>Per-request HTTP timeout, in seconds.</summary>
        public double RequestTimeoutSeconds { get; set; } = 10;

        /// <summary>Seconds of inactivity before a new session starts.</summary>
        public double SessionTimeoutSeconds { get; set; } = 1800;

        /// <summary>Emit a session_start event automatically.</summary>
        public bool TrackSessionStart { get; set; } = true;

        /// <summary>
        /// Your own anonymous id, if the game already has one for this device.
        ///
        /// Used for this run and stored only when nothing is stored yet, exactly
        /// as the native SDKs treat it - so an id set once survives the game
        /// dropping the option later. Leave null to let the SDK generate one.
        /// </summary>
        public string AnonymousId { get; set; }

        /// <summary>
        /// Ask for the push permission during initialize.
        ///
        /// Off by default and worth leaving off: a permission prompt in the
        /// first seconds of a game is the one most often denied, and a denial is
        /// far harder to reverse than a delay. Ask when the player has a reason
        /// to say yes.
        /// </summary>
        public bool RequestPushPermissionAtLaunch { get; set; }

        /// <summary>
        /// Retries per request before it is left in the queue for the next
        /// flush. Same default as both native SDKs.
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Base delay between those retries, in MILLISECONDS - the one duration
        /// not in seconds, because both native SDKs already call it
        /// retryBackoffMs and a name must be the same on every platform.
        /// </summary>
        public int RetryBackoffMs { get; set; } = 1000;

        /// <summary>
        /// Let the NATIVE SDK draw HTML in-app messages (the optional Joryio UI
        /// artifacts). Off by default. Scripts written into a message do not
        /// run there - the native SDKs' Content-Security-Policy lets only their
        /// own bridge execute, and the bridge handles buttons and forms. The
        /// name keeps "Js" for parity with the native option. Has no effect on
        /// messages your SetInAppMessageHandler renders - your own web view
        /// decides what runs there.
        /// </summary>
        public bool AllowHtmlJsInAppMessages { get; set; }

        /// <summary>
        /// Turn on SDK authentication: every ingest request carries the token
        /// set here, or later through JoryioSDK.SetSdkAuthenticationToken. Only
        /// for workspaces with SDK authentication enabled in the dashboard.
        /// </summary>
        public bool EnableSdkAuthentication { get; set; }

        /// <summary>
        /// The signed token to start with, when the game already has one at
        /// launch. Usually set later, after login, with SetSdkAuthenticationToken.
        /// </summary>
        public string SdkAuthenticationToken { get; set; }

        /// <summary>
        /// Start opted out: nothing is collected or sent until JoryioSDK.OptIn.
        /// Opting out is stored by the native SDK, so a player who opted out
        /// stays opted out on the next launch even when this is false. Only
        /// OptIn lifts an opt-out - granting consent does not.
        /// </summary>
        public bool OptOut { get; set; }

        /// <summary>
        /// The answer from your consent screen, if you have one at launch.
        ///
        /// Null (the default) means "not set here": the native SDK keeps the
        /// answer it remembered - a Denied from an earlier launch STAYS denied -
        /// and with nothing remembered it tracks normally (a game with no
        /// consent screen). This used to default to Granted, which lifted a
        /// stored denial on every launch (SDK contract 24, consent model A).
        ///
        /// Granted tracks normally and replaces a remembered Denied. Pending
        /// queues events on the device and sends nothing - no session start, no
        /// in-app sync, no push registration - until
        /// JoryioSDK.UpdateConsent(TrackingConsent.Granted) releases them; it
        /// applies to this launch only and never overrides a remembered Denied.
        /// Denied collects nothing, drops what was queued, and is remembered.
        ///
        /// Consent and OptOut are separate switches: data is collected only
        /// when consent is granted AND the player has not opted out.
        /// </summary>
        public TrackingConsent? TrackingConsent { get; set; }

        /// <summary>
        /// ANDROID ONLY. The notification channel's name as the player sees it
        /// in the system's notification settings (Android 8+). Pass it
        /// localised. Null keeps the native default, "Notifications". iOS has
        /// no notification channels and ignores it.
        /// </summary>
        public string NotificationChannelName { get; set; }

        internal string ToJson() => ToJson(null);

        /// <param name="trackingConsentOverride">
        /// A wire consent sent in place of TrackingConsent - a quiet start's
        /// "pending" (rule 66). The game's own setting is left untouched.
        /// </param>
        internal string ToJson(string trackingConsentOverride)
        {
            // Through the shared writer rather than a hand-built string, so a
            // value with a quote in it (AnonymousId is caller-supplied) is
            // escaped rather than producing JSON the bridge cannot parse.
            var fields = new Dictionary<string, object>
            {
                { "debug", Debug },
                { "batchSize", BatchSize },
                { "flushIntervalSeconds", FlushIntervalSeconds },
                { "sendImmediately", SendImmediately },
                { "maxQueueSize", MaxQueueSize },
                { "requestTimeoutSeconds", RequestTimeoutSeconds },
                { "sessionTimeoutSeconds", SessionTimeoutSeconds },
                { "trackSessionStart", TrackSessionStart },
                { "requestPushPermissionAtLaunch", RequestPushPermissionAtLaunch },
                { "maxRetries", MaxRetries },
                { "retryBackoffMs", RetryBackoffMs },
                { "allowHtmlJsInAppMessages", AllowHtmlJsInAppMessages },
                { "enableSdkAuthentication", EnableSdkAuthentication },
                { "optOut", OptOut },
            };
            // Absent rather than null: the bridges read "no key" as "generate
            // one", and an explicit null would have to be special-cased twice.
            if (!string.IsNullOrEmpty(AnonymousId)) fields["anonymousId"] = AnonymousId;
            if (!string.IsNullOrEmpty(SdkAuthenticationToken)) fields["sdkAuthenticationToken"] = SdkAuthenticationToken;
            if (!string.IsNullOrEmpty(NotificationChannelName)) fields["notificationChannelName"] = NotificationChannelName;

            // Absent, not "granted", when the game did not set it: the natives
            // read "no key" as unset, which keeps a remembered denial. An
            // explicit "granted" here would lift it on every launch.
            if (trackingConsentOverride != null)
            {
                fields["trackingConsent"] = trackingConsentOverride;
            }
            else if (TrackingConsent.HasValue)
            {
                var wire = TrackingConsent.Value.ToWire();
                if (wire != null) fields["trackingConsent"] = wire;
                else global::UnityEngine.Debug.LogWarning(
                    $"[Joryio] JoryioConfig.TrackingConsent has an unknown value ({(int)TrackingConsent.Value}); treated as not set.");
            }
            return JoryioJson.Serialize(fields);
        }
    }
}
