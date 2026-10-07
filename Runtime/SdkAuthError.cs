using System;

namespace Joryio.Runtime
{
    /// <summary>
    /// Why an ingest request was rejected under SDK authentication. Handed to
    /// the handler installed with JoryioSDK.SetSdkAuthenticationErrorHandler.
    ///
    /// The same four fields as the native SdkAuthError on Android and iOS and
    /// React Native's. Field names are lower-case because JsonUtility maps them
    /// from the bridge's JSON verbatim, as PushRegistrationResult does.
    /// </summary>
    [Serializable]
    public class SdkAuthError
    {
        /// <summary>
        /// missing, invalid, expired, sub_mismatch, or unknown - the wire
        /// spelling, the same on every platform.
        /// </summary>
        public string reason;

        /// <summary>
        /// The server's own reason string. Worth logging whenever reason is
        /// "unknown": that only means THIS SDK version did not recognise it.
        /// </summary>
        public string rawReason;

        /// <summary>The ingest endpoint whose request was rejected.</summary>
        public string endpoint;

        /// <summary>
        /// The SDK has stopped retrying this request: no fresh token arrived,
        /// or one did and was rejected too. False on the first notification,
        /// which is the SDK ASKING for a token rather than giving up.
        /// </summary>
        public bool refreshExhausted;
    }
}
