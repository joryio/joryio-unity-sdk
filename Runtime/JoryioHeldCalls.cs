using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Joryio.Runtime
{
    // ── Calls held before Initialize ─────────────────────────────────────────
    //
    // Most calls made before Initialize are ignored with a warning. A few are
    // not, because dropping them would act against what the player asked for:
    //
    // - WITHDRAWALS (contract v4 36): an unsubscribe, leaving a subscription
    //   group, and UnregisterPush. Held, sent once Initialize succeeds, and
    //   never discarded by an OptOut, a Denied consent or a WipeData made in
    //   the meantime.
    // - Subscription changes in the other direction (subscribe, opt in, join a
    //   group). Held too, so the player's LAST choice for a channel or group is
    //   the one that is sent (rule 52): an unsubscribe followed by a subscribe
    //   used to send the unsubscribe and drop the subscribe. These are not
    //   withdrawals, so an OptOut, a Denied consent or a WipeData before
    //   Initialize discards them.
    // - A WipeData made while withdrawals are held. It is held in its place in
    //   the sequence and replayed after the withdrawals made before it, under
    //   a quiet start (see StartNative), so those withdrawals carry the
    //   identity of the player who made them. The native SDKs order a
    //   pre-init wipe after the calls made before it as well; the C# copy has
    //   to do the same for what it holds itself.
    //
    // PERSISTED (contract 22 / 36): a game killed before Initialize used to
    // lose them. Kept in a small file under Application.persistentDataPath -
    // a plain file rather than PlayerPrefs, which on iOS is NSUserDefaults, an
    // API Apple's privacy manifest rules ask a reason for.
    //
    // A FILE THAT CANNOT BE READ IS NOT AN EMPTY ONE (rule 68). On iOS the
    // file is unreadable after a reboot until the player first unlocks the
    // device, and a silent push can launch the game before that. Treating the
    // failed read as "nothing held" used to delete the saved withdrawals at the
    // next save. Now new calls are kept in memory, the file is left alone, and
    // once it can be read the saved calls are put in front of them. When
    // Initialize comes first, the whole start waits for the file (rule 93,
    // "A start while the saved copy cannot be read" below).

    public static partial class JoryioSDK
    {
        /// <summary>
        /// The shared pre-init cap (contract 20). Rule 51: a withdrawal is
        /// dropped only when everything held is a withdrawal, and a held wipe
        /// never is.
        /// </summary>
        internal const int MaxHeldCalls = 1000;

        private static readonly object HeldLock = new object();
        private static readonly List<HeldCall> _held = new List<HeldCall>();

        /// <summary>The saved copy has been read (or there was none).</summary>
        private static bool _heldLoaded;

        /// <summary>
        /// Changes made while the saved copy could not be read, in order. They
        /// are applied again on top of it once it can be, so a newer change
        /// still replaces an older saved one (rule 52) and a pre-init OptOut
        /// still discards a saved subscribe.
        /// </summary>
        private static readonly List<Func<bool>> _changesWhileUnread = new List<Func<bool>>();

        // ── A start while the saved copy cannot be read (rule 93) ───────────
        //
        // An iOS launch before the device's first unlock since boot: the file
        // is protected until then, and a silent push can start the game - and
        // Initialize - before it. Rounds 6-8 started normally and merged the
        // saved calls in LATE, once readable; every version of that merge had
        // an ordering bug, because the saved calls are OLDER than what the
        // game had sent meanwhile and the native SDK stamps a call with the
        // identity current when it ARRIVES.
        //
        // Now nothing is merged late. Such a start is DEFERRED:
        // - The native SDK is started QUIETLY, with consent Pending (in the
        //   config and by a pre-init UpdateConsent, as for a held wipe, rule
        //   66): no Session Start, no in-app sync, no push registration, no
        //   event sent, until the saved decisions have been replayed. It is
        //   started at all - rather than left off until the unlock - so its
        //   own push handling (a notification opened at the unlock) and its
        //   iOS deferred-start machinery are in place; Pending keeps it from
        //   collecting anything meanwhile. Pending is never stored and never
        //   lifts a remembered Denied.
        // - Nothing held is sent, and every call the game makes after
        //   Initialize that reaches the native SDK is kept in memory, in call
        //   order (CallsWhileDeferred). Getters still answer from the native
        //   SDK, except IsUserOptedOut and GetTrackingConsent, which also
        //   count the decisions waiting here and the explicit ones this launch
        //   made (a pre-init call, the config). With none, GetTrackingConsent
        //   reports Pending: the stored decision cannot be read yet (scan 10,
        //   B1).
        // - The consent handed back is decided only when the start FINISHES,
        //   never at Initialize: before the unlock the native storage reads
        //   as empty too, so the native getter answers Granted over a stored
        //   Denied (scan 10, B1). It is, in this order: a pre-init
        //   UpdateConsent; else the config's explicit TrackingConsent; else
        //   the decision saved on the device, read from the native SDK only
        //   now, after the unlock (owner decision 2026-10-07, "option C"):
        //   a stored Denied stays Denied, and with none stored it is what a
        //   normal launch starts with - Granted (ConsentAfterDeferredStart).
        // - Once the file can be read (the next public call, or the game
        //   coming to the foreground - RetrySavedHeldCalls), it is replayed
        //   exactly as a readable start would have: the saved calls, then
        //   what this launch held before Initialize (in that order, with its
        //   changes applied on top), then the consent the game started with
        //   is handed back, then the calls made since, in order.
        // So the native SDK sees the same sequence as a start after the
        // unlock - correct by construction, with no heuristics about keys or
        // identities. The cost: what the game does before the unlock reaches
        // the native SDK only at the unlock (and, held in memory, is lost if
        // the game is killed first - like the native SDK's own queue of calls
        // during its deferred start).

        /// <summary>The start was deferred: the saved copy could not be read at Initialize.</summary>
        private static volatile bool _startDeferred;

        /// <summary>The deferred start is being replayed (a call made from inside it queues behind).</summary>
        private static bool _finishingDeferredStart;

        /// <summary>Calls made after Initialize while the start is deferred, in call order.</summary>
        private static readonly List<DeferredCall> _callsWhileDeferred = new List<DeferredCall>();

        /// <summary>An OptOut / OptIn and UpdateConsent waiting in that list - what the getters report.</summary>
        private static bool? _optOutWhileDeferred;
        private static TrackingConsent? _consentWhileDeferred;

        private sealed class DeferredCall
        {
            internal string Method;
            internal Action Run;
            /// <summary>An event-like call: dropped first when the list is full.</summary>
            internal bool Data;
            /// <summary>A withdrawal or a privacy decision: dropped last (rule 51).</summary>
            internal bool Keep;
            /// <summary>Settles a caller's callback when the call is dropped.</summary>
            internal Action OnDropped;
        }

        /// <summary>
        /// The saved copy was replayed but could not be cleared (scan 6, N8).
        /// Retried until it is, so a stale subscribe is not replayed again at
        /// the next launch over a newer choice.
        /// </summary>
        private static bool _heldClearOwed;

        private static bool _heldReadWarned;

        /// <summary>Where held calls survive a restart. Replaceable for tests.</summary>
        internal static IHeldCallStore HeldStore = new FileHeldCallStore();

        /// <summary>Snapshot of what is held, in order. Tests.</summary>
        internal static List<HeldCall> HeldForTesting()
        {
            lock (HeldLock)
            {
                if (!_startDeferred) EnsureHeldLoaded();
                return new List<HeldCall>(_held);
            }
        }

        /// <summary>Forget the in-memory copy, as a process restart would. Tests.</summary>
        internal static void SimulateRestartForTesting()
        {
            lock (HeldLock)
            {
                ResetHeldState();
                _initialized = false;
                _optOutBeforeInit = null;
                _consentBeforeInit = null;
                _lastConsent = null;
                _quietStartConsent = null;
            }
        }

        private static void ResetHeldState()
        {
            _held.Clear();
            _heldLoaded = false;
            _changesWhileUnread.Clear();
            _startDeferred = false;
            _finishingDeferredStart = false;
            _callsWhileDeferred.Clear();
            _optOutWhileDeferred = null;
            _consentWhileDeferred = null;
            _deferredConfigConsent = null;
            _deferredConfigOptOut = false;
            _heldClearOwed = false;
            _heldReadWarned = false;
        }

        /// <summary>
        /// Rule 73: before Initialize, is collection refused right now - the
        /// player opted out, or consent is Denied? This launch's own call
        /// wins over the stored decision; a Pending never reopens a stored
        /// Denied (contract 45).
        /// </summary>
        private static bool CollectionRefusedBeforeInit()
        {
            bool optedOut;
            if (_optOutBeforeInit.HasValue) optedOut = _optOutBeforeInit.Value;
            else
            {
                try { optedOut = Native.IsUserOptedOut(); }
                catch (Exception) { optedOut = false; }
            }
            if (optedOut) return true;
            if (_consentBeforeInit == TrackingConsent.Denied) return true;
            if (_consentBeforeInit == TrackingConsent.Granted) return false;
            string stored = null;
            try { stored = Native.GetTrackingConsent(); }
            catch (Exception) { stored = null; }
            return TrackingConsentWire.FromWire(stored) == TrackingConsent.Denied;
        }

        /// <summary>
        /// Read the saved copy once. False while it cannot be read: nothing is
        /// saved or deleted until it can (rule 68).
        /// </summary>
        private static bool EnsureHeldLoaded()
        {
            if (_heldLoaded) return true;
            string saved;
            try { saved = HeldStore?.Load(); }
            catch (Exception e)
            {
                if (!_heldReadWarned)
                {
                    _heldReadWarned = true;
                    Debug.LogWarning($"[Joryio] Could not read the calls held before Initialize ({e.Message}); new ones are kept in memory and the saved ones are left untouched until they can be read.");
                }
                return false;
            }
            _heldLoaded = true;
            if (_changesWhileUnread.Count == 0)
            {
                // Saved ones are older than anything this process held already.
                _held.InsertRange(0, HeldCall.Decode(saved));
                return true;
            }
            var changes = _changesWhileUnread.ToArray();
            _changesWhileUnread.Clear();
            _held.Clear();
            _held.AddRange(HeldCall.Decode(saved));
            foreach (var change in changes) change();
            PersistHeld();
            return true;
        }

        /// <summary>Apply a change to what is held, remember it while unread, and save.</summary>
        private static void ChangeHeld(Func<bool> change)
        {
            var changed = change();
            if (!_heldLoaded) _changesWhileUnread.Add(change);
            else if (changed) PersistHeld();
        }

        private static void PersistHeld()
        {
            // Never over a copy that could not be read: it may hold
            // withdrawals this process has not seen.
            if (!_heldLoaded) return;
            try
            {
                if (_held.Count == 0) HeldStore?.Save(null);
                else HeldStore?.Save(HeldCall.Encode(_held));
            }
            catch (Exception e) { Debug.LogWarning($"[Joryio] Could not save the calls held before Initialize: {e.Message}"); }
        }

        /// <summary>
        /// Hold <paramref name="call"/> until Initialize. False when the SDK
        /// is already running: the caller sends it now.
        /// </summary>
        private static bool TryHold(HeldCall call)
        {
            lock (HeldLock)
            {
                if (_initialized)
                {
                    // Sent by the caller - through Call, which keeps it in
                    // order behind the saved calls while the start is
                    // deferred (rule 93). A saved copy that could not be
                    // cleared is cleared first (scan 6, N8).
                    RetrySavedHeldCallsLocked();
                    return false;
                }
                // Rule 73: a subscribe (or opt-in, or group join) made while
                // the player is opted out or consent is Denied is dropped now,
                // not held - a later OptIn or Granted must not send a choice
                // made while collection was refused. A withdrawal is always
                // held (contract 36).
                if (!call.IsWithdrawal && CollectionRefusedBeforeInit())
                {
                    Debug.LogWarning($"[Joryio] {call.Method} before Initialize while the player is opted out or consent is Denied: dropped, and not sent by a later OptIn or Granted consent.");
                    return true;
                }
                EnsureHeldLoaded();
                ChangeHeld(() =>
                {
                    // A newer change of the same channel, group or push state
                    // replaces an older held one - but only for the same
                    // person: past a held wipe the identity is a new one
                    // (rule 52).
                    var since = LastWipeIndex() + 1;
                    for (var i = _held.Count - 1; i >= since; i--)
                    {
                        if (_held[i].Key == call.Key) _held.RemoveAt(i);
                    }
                    if (_held.Count >= MaxHeldCalls) EvictOneHeld();
                    _held.Add(call);
                    return true;
                });
            }
            Debug.Log($"[Joryio] {call.Method} before Initialize: held, and sent when the SDK starts.");
            return true;
        }

        private static int LastWipeIndex()
        {
            for (var i = _held.Count - 1; i >= 0; i--) if (_held[i].Kind == HeldCall.Wipe) return i;
            return -1;
        }

        /// <summary>Rule 51: the oldest non-withdrawal first; a withdrawal only when nothing else is left; never a wipe.</summary>
        private static void EvictOneHeld()
        {
            var victim = _held.FindIndex(c => !c.IsWithdrawal && c.Kind != HeldCall.Wipe);
            if (victim < 0) victim = _held.FindIndex(c => c.Kind != HeldCall.Wipe);
            if (victim < 0) return;
            Debug.LogWarning($"[Joryio] More than {MaxHeldCalls} calls held before Initialize; dropped the oldest ({_held[victim].Method}).");
            _held.RemoveAt(victim);
            // Two wipes with nothing left between them are one wipe. Without
            // this, wipes - which are never evicted - could outgrow the cap.
            if (victim > 0 && victim < _held.Count
                && _held[victim - 1].Kind == HeldCall.Wipe && _held[victim].Kind == HeldCall.Wipe)
            {
                _held.RemoveAt(victim);
            }
        }

        /// <summary>
        /// A pre-init OptOut, Denied consent or WipeData: held subscribes go
        /// (they collect), held withdrawals stay (contract 36).
        /// </summary>
        private static void DiscardHeldNonWithdrawals()
        {
            lock (HeldLock)
            {
                if (_initialized) return;
                EnsureHeldLoaded();
                ChangeHeld(() => _held.RemoveAll(c => !c.IsWithdrawal && c.Kind != HeldCall.Wipe) > 0);
            }
        }

        /// <summary>
        /// WipeData before Initialize. True when the wipe was HELD in its place
        /// behind held withdrawals, so it must not reach the native SDK yet.
        /// </summary>
        private static bool HoldWipeIfNeeded()
        {
            lock (HeldLock)
            {
                if (_initialized) return false;
                var loaded = EnsureHeldLoaded();
                // An unreadable saved copy may hold withdrawals, so the wipe
                // waits behind it rather than erasing their identity first.
                var hold = !loaded || _held.Exists(c => c.IsWithdrawal);
                var wipe = new HeldCall(HeldCall.Wipe, null, null, null);
                ChangeHeld(() =>
                {
                    // Erased with everything else: a held subscribe collects.
                    var changed = _held.RemoveAll(c => !c.IsWithdrawal && c.Kind != HeldCall.Wipe) > 0;
                    if (!hold) return changed;
                    // A wipe straight after a held wipe adds nothing - one
                    // more new identity, one more push unregister - and used to
                    // pile up past the cap (scan 6, N3).
                    if (_held.Count > 0 && _held[_held.Count - 1].Kind == HeldCall.Wipe) return changed;
                    if (_held.Count >= MaxHeldCalls) EvictOneHeld();
                    _held.Add(wipe);
                    return true;
                });
                if (!hold) return false;
            }
            Debug.Log("[Joryio] WipeData before Initialize: held behind the withdrawals made before it, so they still reach the player who asked; the wipe runs as the SDK starts.");
            return true;
        }

        /// <summary>
        /// The consent a quiet start hands back once the held calls are sent,
        /// computed once (a failed Initialize retried must not recompute it
        /// from the Pending the first attempt set). An UpdateConsent made
        /// before the retry clears it, so the newer choice is the one handed
        /// back (scan 6, N2).
        /// </summary>
        private static string _quietStartConsent;

        /// <summary>
        /// The consent the native SDK would have started with: an explicit
        /// UpdateConsent before Initialize, else the config's explicit value,
        /// else what is stored (Denied stays Denied, nothing means Granted).
        /// </summary>
        private static string ConsentAfterQuietStart(JoryioConfig config)
        {
            if (_consentBeforeInit.HasValue) return _consentBeforeInit.Value.ToWire();
            var configured = config?.TrackingConsent;
            if (configured.HasValue && configured.Value.ToWire() != null) return configured.Value.ToWire();
            string stored = null;
            try { stored = Native.GetTrackingConsent(); }
            catch (Exception) { stored = null; }
            var parsed = TrackingConsentWire.FromWire(stored);
            return parsed == TrackingConsent.Denied ? "denied"
                : parsed == TrackingConsent.Pending ? "pending"
                : "granted";
        }

        /// <summary>
        /// Start the native SDK and send what was held, in order. Internal for
        /// tests: Initialize adds only the key checks and the callback object.
        ///
        /// A QUIET START when a wipe is held behind withdrawals. The native
        /// SDK is started with consent Pending - in the config as well as by a
        /// pre-init UpdateConsent, so neither a game's explicit Granted nor a
        /// pre-init grant meeting a stored Denial can start it collecting
        /// (rule 66). Pending means no Session Start, no in-app sync, no push
        /// registration (contract 4), so the original identity is still in
        /// place when the withdrawals are replayed; they go into the native
        /// outbox carrying it, and a wipe keeps them (contract 36). Then the
        /// held wipe runs, and the consent the game would have started with is
        /// handed back: the Session Start and everything else happen once,
        /// under the new identity. Pending is never stored, and never lifts a
        /// remembered Denied.
        ///
        /// An iOS launch before the device's first unlock: the native SDK
        /// defers its own start to the unlock and queues calls meanwhile.
        /// When the saved file cannot be read yet either, the start is
        /// DEFERRED (rule 93, see the top of this file): the native SDK is
        /// started quietly and nothing held, saved or made since is sent until
        /// the file can be read (RetrySavedHeldCalls).
        /// </summary>
        internal static void StartNative(string sdkKey, string apiHost, JoryioConfig config)
        {
            lock (HeldLock)
            {
                var loaded = EnsureHeldLoaded();
                // Unreadable: the saved copy may hold withdrawals and a wipe,
                // so it is a quiet start too - nothing is collected under the
                // stored identity before the saved decisions are replayed.
                var quiet = !loaded || _held.Exists(c => c.Kind == HeldCall.Wipe);
                var configJson = (config ?? new JoryioConfig()).ToJson(quiet ? "pending" : null);
                if (quiet)
                {
                    // Unreadable: the consent is decided when the start
                    // finishes (FinishDeferredStartLocked), not here - the
                    // native storage cannot be read yet either (scan 10, B1).
                    if (!loaded) _quietStartConsent = null;
                    else if (_quietStartConsent == null) _quietStartConsent = ConsentAfterQuietStart(config);
                    Native.UpdateConsent("pending");
                }
                Native.Initialize(sdkKey, apiHost, configJson);
                _initialized = true;
                RememberConfiguredConsent(config);

                if (!loaded)
                {
                    // Deferred: what is held stays held (in front of the
                    // saved copy's changes, _changesWhileUnread), the file is
                    // left untouched, and the config's explicit consent is
                    // kept for the hand-back.
                    var configured = config?.TrackingConsent;
                    _deferredConfigConsent = configured.HasValue && configured.Value.ToWire() != null ? configured : null;
                    _deferredConfigOptOut = config != null && config.OptOut;
                    _startDeferred = true;
                    Debug.Log("[Joryio] Started before the calls saved by an earlier launch can be read (iOS before the first unlock): the SDK collects nothing, and every call waits, until they can be read; then they are sent first, and the calls made since after them, in order.");
                    return;
                }

                var held = _held.ToArray();
                _held.Clear();
                _heldClearOwed = true;
                TryClearSavedHeld();
                foreach (var call in held) Call(call.Method, () => call.Send(Native));
                if (quiet) HandBackQuietStartConsent();
            }
        }

        /// <summary>
        /// After a quiet start's held calls: hand back the consent the game
        /// would have started with. Pending: already there (or Denied, which
        /// Pending never reopens).
        /// </summary>
        private static void HandBackQuietStartConsent()
        {
            var restore = _quietStartConsent;
            _quietStartConsent = null;
            if (restore == null) return;
            string now = null;
            try { now = Native.GetTrackingConsent(); }
            catch (Exception) { now = null; }
            if (restore != "pending" && restore != now)
            {
                Invoke(nameof(UpdateConsent), () => Native.UpdateConsent(restore));
            }
        }

        private static void TryClearSavedHeld()
        {
            if (!_heldClearOwed) return;
            try
            {
                HeldStore?.Save(null);
                _heldClearOwed = false;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Joryio] Could not clear the calls held before Initialize after sending them ({e.Message}); trying again later so they are not sent twice.");
            }
        }

        /// <summary>
        /// After the SDK started: finish a deferred start once the saved copy
        /// can be read (rule 93), and clear a saved copy that could not be
        /// cleared (scan 6, N8). Runs at every call that reaches the native
        /// SDK while the start is deferred, before a subscription change,
        /// push unregister, wipe, identify or reset, and when the game returns
        /// to the foreground.
        /// </summary>
        internal static void RetrySavedHeldCalls()
        {
            lock (HeldLock) RetrySavedHeldCallsLocked();
        }

        private static void RetrySavedHeldCallsLocked()
        {
            if (!_initialized) return;
            if (_startDeferred)
            {
                FinishDeferredStartLocked();
                return;
            }
            TryClearSavedHeld();
        }

        /// <summary>
        /// The saved copy can be read now: replay the deferred start as a
        /// readable start would have run it - the saved calls, then this
        /// launch's held ones (EnsureHeldLoaded puts the saved copy in front
        /// and applies this launch's changes on top), then the consent handed
        /// back, then every call made since, in call order.
        /// </summary>
        private static void FinishDeferredStartLocked()
        {
            if (_finishingDeferredStart) return; // a call from inside the replay: it is queued behind
            if (!EnsureHeldLoaded()) return;     // still unreadable; next time
            _finishingDeferredStart = true;
            try
            {
                var held = _held.ToArray();
                _held.Clear();
                _heldClearOwed = true;
                TryClearSavedHeld();
                foreach (var call in held) Invoke(call.Method, () => call.Send(Native));
                _quietStartConsent = ConsentAfterDeferredStart();
                HandBackQuietStartConsent();
                // By index: a call made from inside one of these is appended
                // and still runs in its place.
                for (var i = 0; i < _callsWhileDeferred.Count; i++)
                {
                    var later = _callsWhileDeferred[i];
                    Invoke(later.Method, later.Run);
                }
            }
            finally
            {
                _callsWhileDeferred.Clear();
                _optOutWhileDeferred = null;
                _consentWhileDeferred = null;
                _deferredConfigConsent = null;
                _deferredConfigOptOut = false;
                _finishingDeferredStart = false;
                _startDeferred = false;
            }
        }

        /// <summary>
        /// While the start is deferred, keep <paramref name="call"/> in order
        /// behind the saved calls and return true; otherwise false, and the
        /// caller runs it now. The file is tried first, so a call made after
        /// the unlock finishes the start and then runs in its place.
        /// </summary>
        private static bool DeferIfStartPending(string method, Action call, bool keep, Action onDropped)
        {
            if (!_startDeferred) return false;
            lock (HeldLock)
            {
                if (!_startDeferred) return false;
                if (!_finishingDeferredStart) FinishDeferredStartLocked();
                if (!_startDeferred) return false;
                if (_callsWhileDeferred.Count >= MaxHeldCalls) DropOneDeferredCall();
                _callsWhileDeferred.Add(new DeferredCall
                {
                    Method = method,
                    Run = call,
                    Data = IsDataMethod(method),
                    Keep = keep || IsDecisionMethod(method),
                    OnDropped = onDropped,
                });
                return true;
            }
        }

        /// <summary>The shared cap (contract 20, rule 51): an event-like call first, then anything but a withdrawal or decision, then the oldest.</summary>
        private static void DropOneDeferredCall()
        {
            var victim = _callsWhileDeferred.FindIndex(c => c.Data);
            if (victim < 0) victim = _callsWhileDeferred.FindIndex(c => !c.Keep);
            if (victim < 0) victim = 0;
            var dropped = _callsWhileDeferred[victim];
            _callsWhileDeferred.RemoveAt(victim);
            Debug.LogWarning($"[Joryio] More than {MaxHeldCalls} calls waiting for the saved calls to be readable; dropped the oldest ({dropped.Method}).");
            try { dropped.OnDropped?.Invoke(); }
            catch (Exception e) { Debug.LogError($"[Joryio] Callback threw: {e}"); }
        }

        private static bool IsDataMethod(string method) =>
            method.StartsWith("Ecommerce.", StringComparison.Ordinal)
            || Array.IndexOf(DataMethods, method) >= 0;

        private static readonly string[] DataMethods =
        {
            nameof(Track), nameof(TrackScreen), nameof(SetAttributes), nameof(SetAttribute),
            nameof(IncrementAttribute), nameof(UnsetAttribute), nameof(AddAlias), nameof(Flush),
            nameof(TrackInAppImpression), nameof(SubmitInAppForm), nameof(SyncInAppCampaigns),
            nameof(EvaluateInAppCampaigns), nameof(ResetDisplayedCampaigns), nameof(UpdateBadgeCount),
            nameof(ClearBadge), nameof(ReportPushDelivered), nameof(TrackPushClick),
        };

        private static bool IsDecisionMethod(string method) =>
            method == nameof(OptOut) || method == nameof(OptIn) || method == nameof(UpdateConsent)
            || method == nameof(WipeData) || method == nameof(UnregisterPush) || method == nameof(RemoveFromSubscriptionGroup);

        /// <summary>An OptOut or OptIn waiting behind a deferred start: what IsUserOptedOut reports meanwhile.</summary>
        private static void NoteOptOutWhileDeferred(bool optedOut)
        {
            lock (HeldLock) if (_startDeferred && !_finishingDeferredStart) _optOutWhileDeferred = optedOut;
        }

        /// <summary>An UpdateConsent waiting behind a deferred start: what GetTrackingConsent reports meanwhile.</summary>
        private static void NoteConsentWhileDeferred(TrackingConsent consent)
        {
            lock (HeldLock) if (_startDeferred && !_finishingDeferredStart) _consentWhileDeferred = consent;
        }

        /// <summary>The config's explicit TrackingConsent, kept while the start is deferred.</summary>
        private static TrackingConsent? _deferredConfigConsent;

        /// <summary>The config's OptOut = true, kept while the start is deferred.</summary>
        private static bool _deferredConfigOptOut;

        /// <summary>
        /// This launch's explicit consent for a deferred start: a pre-init
        /// UpdateConsent, else the config's explicit TrackingConsent.
        /// </summary>
        private static TrackingConsent? ExplicitConsentForDeferredStart() =>
            _consentBeforeInit ?? _deferredConfigConsent;

        /// <summary>
        /// The consent a deferred start hands back, worked out when it
        /// FINISHES - the saved copy is readable, so the device has been
        /// unlocked (owner decision 2026-10-07, "option C"):
        /// 1. a pre-init UpdateConsent; 2. else the config's explicit
        /// TrackingConsent; 3. else the decision saved on the device, read
        /// from the native SDK now. Never earlier: before the unlock its
        /// storage reads as empty, and the getter answers Granted over a
        /// stored Denied (scan 10, B1).
        ///
        /// Both native SDKs store only a denial, so the getter tells the
        /// two cases apart: "denied" is a stored Denied, and it stays.
        /// Anything else means none is stored, and a normal launch with an
        /// unset config would start Granted (iOS startConsent(nil, nil,
        /// false) and its pre-init getter; Android's pre-init getter too), so Granted is
        /// handed back - never more than that default. The quiet start set
        /// Pending, so the getter answers "pending" here, or "granted" for a
        /// grant in effect; neither is a stored denial.
        ///
        /// The read is right whether or not the native SDK has finished its
        /// own deferred start at the unlock: before it does, its pre-init
        /// getter reads the stored denial from storage directly (a pre-init
        /// Pending never hides it); once it has, its consent IS the start
        /// consent worked out from the quiet Pending and that same stored
        /// denial, and iOS sets it before it reports itself initialized.
        /// The quiet Pending can never store a denial, so the flag is the
        /// same at every point of that start.
        ///
        /// No answer (a native failure): nothing is handed back, and the
        /// native SDK keeps what it started with (Pending, or a stored
        /// Denied) - an unknown is never turned into a grant.
        /// </summary>
        private static string ConsentAfterDeferredStart()
        {
            var explicitConsent = ExplicitConsentForDeferredStart();
            if (explicitConsent.HasValue) return explicitConsent.Value.ToWire();
            string stored = null;
            try { stored = Native.GetTrackingConsent(); }
            catch (Exception) { stored = null; }
            var parsed = TrackingConsentWire.FromWire(stored);
            if (parsed == null) return null;
            return parsed == TrackingConsent.Denied ? "denied" : "granted";
        }

        /// <summary>
        /// While the start is deferred the native SDK sits at Pending, and its
        /// stored decision cannot be read yet: report a newer UpdateConsent
        /// waiting, else the explicit consent to be handed back, else Pending
        /// (not yet known - never Granted; the stored decision is read only
        /// when the start finishes). Null when not deferred.
        /// </summary>
        private static TrackingConsent? ConsentWhileDeferred()
        {
            lock (HeldLock)
            {
                if (!_startDeferred) return null;
                return _consentWhileDeferred ?? ExplicitConsentForDeferredStart() ?? TrackingConsent.Pending;
            }
        }

        /// <summary>
        /// While deferred: an OptOut / OptIn waiting, else this launch's
        /// pre-init one, else the config's OptOut. Null otherwise (and with
        /// none of them: the native answer is used).
        /// </summary>
        private static bool? OptOutWhileDeferred()
        {
            lock (HeldLock)
            {
                if (!_startDeferred) return null;
                return _optOutWhileDeferred ?? _optOutBeforeInit ?? (_deferredConfigOptOut ? true : (bool?)null);
            }
        }

        /// <summary>The start is deferred until the saved copy can be read. Tests.</summary>
        internal static bool StartDeferredForTesting => _startDeferred;

        /// <summary>Send what is held, in order (without starting anything). Tests.</summary>
        internal static void ReplayHeldWithdrawals()
        {
            HeldCall[] held;
            lock (HeldLock)
            {
                EnsureHeldLoaded();
                held = _held.ToArray();
                _held.Clear();
                PersistHeld();
            }
            foreach (var call in held) Call(call.Method, () => call.Send(Native));
        }
    }

    /// <summary>One call held before Initialize. Plain data, so it can be saved.</summary>
    internal sealed class HeldCall
    {
        internal const string Channel = "channel";
        internal const string Group = "group";
        internal const string Push = "unregisterPush";
        internal const string Wipe = "wipe";

        internal readonly string Kind;
        /// <summary>The subscription channel (Channel, Group).</summary>
        internal readonly string ChannelName;
        /// <summary>Channel: optedIn / subscribed / unsubscribed. Group: subscribe / unsubscribe.</summary>
        internal readonly string Status;
        internal readonly string GroupId;

        internal HeldCall(string kind, string channel, string status, string groupId)
        {
            Kind = kind;
            ChannelName = channel;
            Status = status;
            GroupId = groupId;
        }

        /// <summary>Contract 36: these are delivered whatever happens before Initialize.</summary>
        internal bool IsWithdrawal =>
            Kind == Push
            || (Kind == Channel && Status == "unsubscribed")
            || (Kind == Group && Status == "unsubscribe");

        /// <summary>What a newer call supersedes (rule 52; the identity is the wipe boundary).</summary>
        internal string Key =>
            Kind == Channel ? "channel|" + ChannelName
            : Kind == Group ? "group|" + GroupId + "|" + ChannelName
            : Kind == Push ? "push"
            : "wipe";

        internal string Method =>
            Kind == Channel ? nameof(JoryioSDK.SetSubscription)
            : Kind == Group ? (Status == "unsubscribe" ? nameof(JoryioSDK.RemoveFromSubscriptionGroup) : nameof(JoryioSDK.AddToSubscriptionGroup))
            : Kind == Push ? nameof(JoryioSDK.UnregisterPush)
            : nameof(JoryioSDK.WipeData);

        internal void Send(Platform.IJoryioNative native)
        {
            switch (Kind)
            {
                case Channel: native.SetSubscription(ChannelName, Status); break;
                case Group:
                    if (Status == "unsubscribe") native.RemoveFromSubscriptionGroup(GroupId, ChannelName);
                    else native.AddToSubscriptionGroup(GroupId, ChannelName);
                    break;
                case Push: native.UnregisterPush(); break;
                case Wipe: native.WipeData(); break;
            }
        }

        public override string ToString() =>
            Kind == Channel ? $"{Kind} {ChannelName} {Status}"
            : Kind == Group ? $"{Kind} {GroupId} {ChannelName} {Status}"
            : Kind;

        // One line per call, four escaped fields - no JsonUtility, which needs
        // the engine and cannot be exercised by the managed tests.
        internal static string Encode(IEnumerable<HeldCall> calls)
        {
            var sb = new StringBuilder();
            foreach (var c in calls)
            {
                sb.Append(Esc(c.Kind)).Append('\t').Append(Esc(c.ChannelName)).Append('\t')
                  .Append(Esc(c.Status)).Append('\t').Append(Esc(c.GroupId)).Append('\n');
            }
            return sb.ToString();
        }

        internal static List<HeldCall> Decode(string saved)
        {
            var calls = new List<HeldCall>();
            if (string.IsNullOrEmpty(saved)) return calls;
            var lines = saved.Split('\n');
            // Every complete line ends in a newline, so the piece after the
            // last one is empty - or the torn end of an interrupted write,
            // which could otherwise pass for a call with a cut-off group id.
            for (var i = 0; i < lines.Length - 1; i++)
            {
                var f = lines[i].Split('\t');
                if (f.Length != 4) continue;
                var kind = Unesc(f[0]);
                if (kind != Channel && kind != Group && kind != Push && kind != Wipe) continue;
                calls.Add(new HeldCall(kind, Unesc(f[1]), Unesc(f[2]), Unesc(f[3])));
            }
            return calls;
        }

        // "-" stands for null; everything else is URI-escaped, so no tab,
        // newline or dash-only value can be mistaken for a separator.
        private static string Esc(string s) => s == null ? "-" : "=" + Uri.EscapeDataString(s);
        private static string Unesc(string s) => s == null || s == "-" || s.Length == 0 ? null : Uri.UnescapeDataString(s.Substring(1));
    }

    internal interface IHeldCallStore
    {
        /// <summary>The saved text, or null when there is none. Throws when it cannot be read.</summary>
        string Load();
        /// <summary>Save the text; null deletes it.</summary>
        void Save(string data);
    }

    /// <summary>
    /// A file under Application.persistentDataPath. That property may only be
    /// read on the main thread, so it is captured as the game starts; a call
    /// on another thread before then holds in memory only, with a warning.
    /// </summary>
    internal sealed class FileHeldCallStore : IHeldCallStore
    {
        internal const string FileName = "joryio_held_calls.txt";

        private static string _directory;
        private readonly string _fixedDirectory;

        internal FileHeldCallStore(string directory = null) { _fixedDirectory = directory; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void CaptureDirectory()
        {
            try { _directory = Application.persistentDataPath; }
            catch (Exception) { _directory = null; }
        }

        private string Path
        {
            get
            {
                var dir = _fixedDirectory ?? _directory;
                if (string.IsNullOrEmpty(dir))
                {
                    CaptureDirectory();
                    dir = _directory;
                }
                return string.IsNullOrEmpty(dir) ? null : System.IO.Path.Combine(dir, FileName);
            }
        }

        /// <summary>
        /// The saved text, or null when there is none. THROWS when it cannot
        /// be read - including before the persistent data path is known - so
        /// that an unreadable copy is never taken for an empty one (rule 68).
        /// </summary>
        public string Load()
        {
            var path = Path;
            if (path == null) throw new InvalidOperationException("no persistent data path yet");
            if (File.Exists(path)) return File.ReadAllText(path, Encoding.UTF8);
            // No main copy, but a complete one written aside: a kill came
            // between writing it and moving it into place.
            var temp = path + ".tmp";
            if (File.Exists(temp)) return File.ReadAllText(temp, Encoding.UTF8);
            return null;
        }

        public void Save(string data)
        {
            var path = Path;
            if (path == null) throw new InvalidOperationException("no persistent data path yet; held in memory only");
            var temp = path + ".tmp";
            if (data == null)
            {
                // The aside copy first: left behind, Load would bring it back.
                if (File.Exists(temp)) File.Delete(temp);
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            // Written aside, flushed to the disk, then renamed over the old
            // copy in one step (rule 68). A kill at any point leaves either
            // the previous complete copy or the new one - never half a file,
            // and never no file at all, which the old delete-then-move did.
            var bytes = new UTF8Encoding(false).GetBytes(data);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
    }

    /// <summary>In memory only. Tests.</summary>
    internal sealed class MemoryHeldCallStore : IHeldCallStore
    {
        internal string Data;
        public string Load() => Data;
        public void Save(string data) => Data = data;
    }
}
