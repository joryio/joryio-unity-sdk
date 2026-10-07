namespace Joryio.Runtime
{
    /// <summary>
    /// Whether this player has agreed to be tracked - the answer from your
    /// consent screen. The same three states as the native SDKs'
    /// TrackingConsent, sent to them as "granted", "denied" and "pending".
    ///
    /// Independent of OptOut/OptIn (SDK contract 24, consent model A): data is
    /// collected only when consent is Granted AND the player has not opted
    /// out. Granting consent never lifts an opt-out, and opting out never
    /// changes the consent.
    /// </summary>
    public enum TrackingConsent
    {
        /// <summary>Track normally.</summary>
        Granted,

        /// <summary>
        /// Collect nothing, and drop anything queued. Remembered across
        /// restarts until Granted is set explicitly.
        /// </summary>
        Denied,

        /// <summary>
        /// Queue on the device and send nothing until consent is granted - for
        /// a consent screen the player has not answered yet. For this launch
        /// only, and never overrides a remembered Denied.
        /// </summary>
        Pending,
    }

    internal static class TrackingConsentWire
    {
        /// <summary>
        /// Lower-case wire values, spelled out rather than derived from the enum
        /// name, so renaming a member here cannot change what the bridges read.
        ///
        /// Null for a value outside the enum - (TrackingConsent)7 compiles in
        /// C#. That used to fall through to "granted", so a cast bug in the game
        /// silently GRANTED consent; the callers now warn and change nothing.
        /// </summary>
        internal static string ToWire(this TrackingConsent consent)
        {
            switch (consent)
            {
                case TrackingConsent.Granted: return "granted";
                case TrackingConsent.Denied: return "denied";
                case TrackingConsent.Pending: return "pending";
                default: return null;
            }
        }

        /// <summary>The native answer ("granted" / "denied" / "pending") back to the enum; null for anything else.</summary>
        internal static TrackingConsent? FromWire(string wire)
        {
            switch (wire)
            {
                case "granted": return TrackingConsent.Granted;
                case "denied": return TrackingConsent.Denied;
                case "pending": return TrackingConsent.Pending;
                default: return null;
            }
        }
    }
}
