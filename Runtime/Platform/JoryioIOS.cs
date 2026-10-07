#if UNITY_IOS && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;

namespace Joryio.Runtime.Platform
{
    /// <summary>
    /// P/Invoke into the Swift bridge's C symbols.
    ///
    /// The bridge is Swift rather than Objective-C because the iOS SDK exposes
    /// no `@objc` API at all - it is pure Swift, so ObjC cannot reach it and
    /// neither can C# without the `@_cdecl` C symbols the bridge emits.
    ///
    /// BOOLS ARE ONE BYTE. Swift's Bool is a single byte, while the marshaller's
    /// default for bool is the four-byte Win32 BOOL - so a returned Bool read
    /// without [return: MarshalAs(UnmanagedType.I1)] includes three bytes of
    /// whatever happened to be in the register, and "false" can come back true.
    /// Every bool crossing this boundary carries the attribute.
    /// </summary>
    internal sealed class JoryioIOS : IJoryioNative
    {
        [DllImport("__Internal")] private static extern void joryio_initialize(string sdkKey, string apiHost, string configJson);
        [DllImport("__Internal")] private static extern void joryio_set_callback_target(string name);
        [DllImport("__Internal")] private static extern void joryio_track(string name, string propsJson);
        [DllImport("__Internal")] private static extern void joryio_track_screen(string name, string propsJson);
        [DllImport("__Internal")] private static extern void joryio_identify(string userId);
        [DllImport("__Internal")] private static extern void joryio_reset();
        [DllImport("__Internal")] private static extern void joryio_flush();
        [DllImport("__Internal")] private static extern IntPtr joryio_get_user_id();
        [DllImport("__Internal")] private static extern IntPtr joryio_get_anonymous_id();
        [DllImport("__Internal")] private static extern void joryio_free_string(IntPtr p);
        [DllImport("__Internal")] private static extern void joryio_enable_in_app_callback(string capabilitiesJson);
        [DllImport("__Internal")] private static extern void joryio_clear_in_app_callback();
        [DllImport("__Internal")] private static extern void joryio_track_in_app_impression(string campaignId, string action, string elementId);
        [DllImport("__Internal")] private static extern void joryio_add_alias(string aliasLabel, string aliasName);
        [DllImport("__Internal")] private static extern void joryio_set_attributes(string json);
        [DllImport("__Internal")] private static extern void joryio_set_attribute(string key, string valueJson);
        [DllImport("__Internal")] private static extern void joryio_increment_attribute(string key, double by);
        [DllImport("__Internal")] private static extern void joryio_unset_attribute(string key);
        [DllImport("__Internal")] private static extern void joryio_opt_in();
        [DllImport("__Internal")] private static extern void joryio_opt_out();
        [DllImport("__Internal")] private static extern void joryio_update_consent(string consent);
        [DllImport("__Internal")] private static extern void joryio_set_subscription(string channel, string status);
        [DllImport("__Internal")] private static extern void joryio_add_to_subscription_group(string groupId, string channel);
        [DllImport("__Internal")] private static extern void joryio_remove_from_subscription_group(string groupId, string channel);
        [DllImport("__Internal")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool joryio_is_user_opted_out();
        [DllImport("__Internal")] private static extern void joryio_wipe_data();
        [DllImport("__Internal")] private static extern IntPtr joryio_get_tracking_consent();
        [DllImport("__Internal")] private static extern void joryio_register_push_token(string token, int requestId);
        [DllImport("__Internal")] private static extern void joryio_report_push_delivered(string trackingId);
        [DllImport("__Internal")] private static extern void joryio_unregister_push();
        [DllImport("__Internal")] private static extern IntPtr joryio_get_device_token();
        [DllImport("__Internal")] private static extern void joryio_track_push_click(string trackingId);
        [DllImport("__Internal")] private static extern void joryio_request_push_permission(int requestId);
        [DllImport("__Internal")] private static extern void joryio_get_push_permission_status(int requestId);
        [DllImport("__Internal")] private static extern void joryio_is_push_enabled(int requestId);
        [DllImport("__Internal")] private static extern void joryio_update_badge_count(int count);
        [DllImport("__Internal")] private static extern void joryio_clear_badge();
        [DllImport("__Internal")] private static extern IntPtr joryio_get_session_id();
        [DllImport("__Internal")] private static extern IntPtr joryio_get_session_info();
        [DllImport("__Internal")] private static extern void joryio_set_sdk_authentication_token(string token);
        [DllImport("__Internal")] private static extern void joryio_enable_sdk_authentication_errors();
        [DllImport("__Internal")] private static extern void joryio_sync_in_app_campaigns();
        [DllImport("__Internal")] private static extern void joryio_reset_displayed_campaigns();
        [DllImport("__Internal")] private static extern void joryio_evaluate_in_app_campaigns();
        [DllImport("__Internal")] private static extern void joryio_submit_in_app_form(string campaignId, string valuesJson, int requestId);
        [DllImport("__Internal")] private static extern void joryio_ecommerce(string method, string payloadJson);

        /// <summary>
        /// The bridge strdup()s every returned string, so the caller owns it.
        /// Marshal.PtrToStringUTF8 copies into managed memory and then the C
        /// buffer MUST be freed - without this every read leaks, quietly, at a
        /// rate proportional to how often the game asks who the user is.
        /// </summary>
        private static string Consume(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return string.Empty;
            try { return Marshal.PtrToStringUTF8(ptr) ?? string.Empty; }
            finally { joryio_free_string(ptr); }
        }

        public void Initialize(string sdkKey, string apiHost, string configJson) => joryio_initialize(sdkKey, apiHost, configJson);
        public void SetCallbackTarget(string gameObjectName) => joryio_set_callback_target(gameObjectName);
        public void Track(string eventName, string propertiesJson) => joryio_track(eventName, propertiesJson);
        public void TrackScreen(string screenName, string propertiesJson) => joryio_track_screen(screenName, propertiesJson);
        public void Identify(string userId) => joryio_identify(userId);
        public void Reset() => joryio_reset();
        public void Flush() => joryio_flush();
        public string GetUserId() => Consume(joryio_get_user_id());
        public string GetAnonymousId() => Consume(joryio_get_anonymous_id());
        public void AddAlias(string aliasLabel, string aliasName) => joryio_add_alias(aliasLabel, aliasName);
        public void SetAttributes(string attributesJson) => joryio_set_attributes(attributesJson);
        public void SetAttribute(string key, string valueJson) => joryio_set_attribute(key, valueJson);
        public void IncrementAttribute(string key, double by) => joryio_increment_attribute(key, by);
        public void UnsetAttribute(string key) => joryio_unset_attribute(key);
        public void OptIn() => joryio_opt_in();
        public void OptOut() => joryio_opt_out();
        public void UpdateConsent(string consent) => joryio_update_consent(consent);

        public void SetSubscription(string channel, string status) =>
            joryio_set_subscription(channel, status);

        public void AddToSubscriptionGroup(string groupId, string channel) =>
            joryio_add_to_subscription_group(groupId, channel);

        public void RemoveFromSubscriptionGroup(string groupId, string channel) =>
            joryio_remove_from_subscription_group(groupId, channel);
        public bool IsUserOptedOut() => joryio_is_user_opted_out();
        public void WipeData() => joryio_wipe_data();
        public string GetTrackingConsent() => Consume(joryio_get_tracking_consent());
        public void RegisterPushToken(string token, int requestId) => joryio_register_push_token(token, requestId);
        public void ReportPushDelivered(string trackingId) => joryio_report_push_delivered(trackingId);
        public void UnregisterPush() => joryio_unregister_push();
        public string GetDeviceToken() => Consume(joryio_get_device_token());
        public void TrackPushClick(string trackingId) => joryio_track_push_click(trackingId);
        public void RequestPushPermission(int requestId) => joryio_request_push_permission(requestId);
        public void GetPushPermissionStatus(int requestId) => joryio_get_push_permission_status(requestId);
        public void IsPushEnabled(int requestId) => joryio_is_push_enabled(requestId);
        public void UpdateBadgeCount(int count) => joryio_update_badge_count(count);
        public void ClearBadge() => joryio_clear_badge();
        public string GetSessionId() => Consume(joryio_get_session_id());
        public string GetSessionInfo() => Consume(joryio_get_session_info());
        public void SetSdkAuthenticationToken(string token) => joryio_set_sdk_authentication_token(token);
        public void EnableSdkAuthenticationErrors() => joryio_enable_sdk_authentication_errors();
        public void SyncInAppCampaigns() => joryio_sync_in_app_campaigns();
        public void ResetDisplayedCampaigns() => joryio_reset_displayed_campaigns();
        public void EvaluateInAppCampaigns() => joryio_evaluate_in_app_campaigns();
        public void EnableInAppCallback(string capabilitiesJson) => joryio_enable_in_app_callback(capabilitiesJson);
        public void ClearInAppCallback() => joryio_clear_in_app_callback();
        public void TrackInAppImpression(string campaignId, string action, string elementId) => joryio_track_in_app_impression(campaignId, action, elementId);
        public void SubmitInAppForm(string campaignId, string valuesJson, int requestId) => joryio_submit_in_app_form(campaignId, valuesJson, requestId);
        public void Ecommerce(string method, string payloadJson) => joryio_ecommerce(method, payloadJson);
    }
}
#endif
