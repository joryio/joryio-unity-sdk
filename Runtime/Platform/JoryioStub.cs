using UnityEngine;
using Joryio.Runtime;

namespace Joryio.Runtime.Platform
{
    /// <summary>
    /// Editor, desktop, console: the API exists and does nothing.
    ///
    /// Warns ONCE per session rather than per call. Developers spend most of
    /// their time in Play Mode with no native layer present, and an SDK that
    /// floods the console there is one they will rip out; an SDK that throws is
    /// worse. One line telling them why nothing is being sent is the whole job.
    /// </summary>
    internal sealed class JoryioStub : IJoryioNative
    {
        private static bool _warned;

        private static void WarnOnce()
        {
            if (_warned) return;
            _warned = true;
            Debug.LogWarning(
                "[Joryio] No native SDK on this platform (Editor, desktop or console), so calls do nothing. " +
                "Build for iOS or Android to send data. This message appears once per session.");
        }

        public void Initialize(string sdkKey, string apiHost, string configJson) => WarnOnce();
        public void SetCallbackTarget(string gameObjectName) { }
        public void Track(string eventName, string propertiesJson) => WarnOnce();
        public void TrackScreen(string screenName, string propertiesJson) => WarnOnce();
        public void Identify(string userId) => WarnOnce();
        public void Reset() => WarnOnce();
        public void Flush() => WarnOnce();
        public string GetUserId() => string.Empty;
        public string GetAnonymousId() => string.Empty;
        public void AddAlias(string aliasLabel, string aliasName) => WarnOnce();
        public void SetAttributes(string attributesJson) => WarnOnce();
        public void SetAttribute(string key, string valueJson) => WarnOnce();
        public void IncrementAttribute(string key, double by) => WarnOnce();
        public void UnsetAttribute(string key) => WarnOnce();
        public void OptIn() => WarnOnce();
        public void OptOut() => WarnOnce();
        public void UpdateConsent(string consent) => WarnOnce();
        public void SetSubscription(string channel, string status) => WarnOnce();
        public void AddToSubscriptionGroup(string groupId, string channel) => WarnOnce();
        public void RemoveFromSubscriptionGroup(string groupId, string channel) => WarnOnce();
        public bool IsUserOptedOut() => false;
        public void WipeData() => WarnOnce();
        public string GetTrackingConsent() => null;
        public void RegisterPushToken(string token, int requestId) { WarnOnce(); JoryioCallbackReceiver.Resolve(requestId, "{\"success\":false,\"message\":\"Editor stub\",\"isUnauthorized\":false,\"isRetryable\":false}"); }
        public void ReportPushDelivered(string trackingId) => WarnOnce();
        public void UnregisterPush() => WarnOnce();
        public string GetDeviceToken() => string.Empty;
        public void TrackPushClick(string trackingId) => WarnOnce();

        // Answer, rather than going silent. A callback that never fires is a
        // hang in the game's code; "denied" in the Editor is both true and
        // actionable.
        public void RequestPushPermission(int requestId) { WarnOnce(); JoryioCallbackReceiver.Resolve(requestId, "false"); }
        public void GetPushPermissionStatus(int requestId) { WarnOnce(); JoryioCallbackReceiver.Resolve(requestId, "unavailable"); }
        public void IsPushEnabled(int requestId) { WarnOnce(); JoryioCallbackReceiver.Resolve(requestId, "false"); }
        public void UpdateBadgeCount(int count) => WarnOnce();
        public void ClearBadge() => WarnOnce();

        public string GetSessionId() => string.Empty;
        public string GetSessionInfo() => string.Empty;
        public void SetSdkAuthenticationToken(string token) => WarnOnce();
        public void EnableSdkAuthenticationErrors() { }
        public void SyncInAppCampaigns() => WarnOnce();
        public void ResetDisplayedCampaigns() => WarnOnce();
        public void EvaluateInAppCampaigns() => WarnOnce();
        public void EnableInAppCallback(string capabilitiesJson) => WarnOnce();
        public void ClearInAppCallback() { }
        public void TrackInAppImpression(string campaignId, string action, string elementId) => WarnOnce();
        public void SubmitInAppForm(string campaignId, string valuesJson, int requestId) { WarnOnce(); JoryioCallbackReceiver.Resolve(requestId, "{\"ok\":false,\"message\":\"Editor stub\"}"); }
        public void Ecommerce(string method, string payloadJson) => WarnOnce();
    }
}
