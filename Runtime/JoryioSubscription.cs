using System;

namespace Joryio.Runtime
{
    /// <summary>
    /// Returned by every On...Updated listener - <see cref="JoryioSDK.OnCardsUpdated"/>,
    /// <see cref="JoryioSDK.OnPlacementsUpdated"/> and <see cref="JoryioSDK.OnFlagsUpdated"/>:
    /// Cancel() stops the updates. Dispose() is the same, for a using block.
    /// Cancelling twice is harmless.
    ///
    /// One type with the same name and the same cancel on every Joryio SDK
    /// (JoryioSubscription.cancel() on iOS, Android, web and React Native),
    /// pinned by the backend's SDK parity spec.
    /// </summary>
    public sealed class JoryioSubscription : IDisposable
    {
        private Action _onCancel;

        internal JoryioSubscription(Action onCancel) { _onCancel = onCancel; }

        /// <summary>Stop the updates to this handler.</summary>
        public void Cancel()
        {
            var cancel = _onCancel;
            _onCancel = null;
            cancel?.Invoke();
        }

        /// <summary>The same as <see cref="Cancel"/>.</summary>
        public void Dispose() => Cancel();
    }
}
