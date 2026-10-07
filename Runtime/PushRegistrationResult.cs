using System;

namespace Joryio.Runtime
{
    /// <summary>
    /// Outcome of <see cref="JoryioSDK.RegisterPushToken(string, Action{PushRegistrationResult})"/>.
    /// Same four fields as the native PushRegistrationResult on Android and iOS.
    /// Field names are lower-case because JsonUtility maps them from the
    /// native JSON verbatim.
    /// </summary>
    [Serializable]
    public class PushRegistrationResult
    {
        public bool success;
        /// <summary>Provider or transport message on failure; null on success.</summary>
        public string message;
        /// <summary>A bad or missing SDK key - retrying will not fix it.</summary>
        public bool isUnauthorized;
        /// <summary>Transient (offline, DNS, 5xx): worth another try.</summary>
        public bool isRetryable;
    }
}
