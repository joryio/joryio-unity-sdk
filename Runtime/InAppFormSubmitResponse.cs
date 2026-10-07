using System;

namespace Joryio.Runtime
{
    /// <summary>
    /// The server's verdict on a form submitted from an HTML in-app message,
    /// through JoryioSDK.SubmitInAppForm. Same four fields as the native
    /// InAppFormSubmitResponse on Android and iOS. Never an exception: any
    /// failure, including no network, arrives as ok = false.
    /// </summary>
    [Serializable]
    public class InAppFormSubmitResponse
    {
        /// <summary>Accepted and written to the player's profile.</summary>
        public bool ok;

        /// <summary>Text to show the player, when the form has one.</summary>
        public string message;

        /// <summary>Where the form sends the player next, when it has a redirect.</summary>
        public string redirect;

        /// <summary>Per-field validation errors, when ok is false.</summary>
        public string[] errors;
    }
}
