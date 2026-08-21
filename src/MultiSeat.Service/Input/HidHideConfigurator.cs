using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Shared.Models;

namespace MultiSeat.Service.Input;

/// <summary>
/// Per-seat gamepad isolation, via HidHide's undocumented session jail.
///
/// <b>What this replaced, and why.</b> The old design hid every gaming device globally and
/// whitelisted Apollo's executable so it could still see them. That cannot isolate seats from
/// each other even in principle: the whitelist is global and cannot pair an application with a
/// device, so one whitelisted binary sees every hidden pad — and every seat runs the same Apollo
/// binary. It also hid the wrong node (see <see cref="HidHideDevice"/>), and neither defect was
/// ever observable because two independent bugs meant the CLI was never invoked at all.
///
/// The mechanism now is <see cref="HidHideSessionJail"/>: a blacklist entry suffixed with
/// <c>!&lt;sessionId&gt;</c> is visible only inside that session. It acts on the DEVICE rather
/// than on the reader, so it does not care how a game enumerates — including
/// <c>Windows.Gaming.Input</c>, which no local filter can reach.
///
/// <b>Three rules that are not negotiable</b>, each of which cost @jmlopezdona real time in
/// issue #19 before it was understood:
///
///   1. <b>Both nodes.</b> XInput reads the XUSB node, not the HID one.
///   2. <b>Rules must be written BEFORE the pad exists.</b> HidHide filters at open time, so a
///      rule that lands after the pad is late by definition — and <c>dwm</c>, <c>explorer</c> and
///      <c>GameInputSvc</c> of EVERY session open each new pad within that window, keeping handles
///      that never expire. A rule for an absent device matches nothing, so pre-writing is free.
///   3. <b>Ownership is derived, never named.</b> Nothing in the device tree says "ViGEm"; the
///      test is bus versus PnP root. And a placement made by elimination is never remembered and
///      always said out loud — his worst failure of the week was a free seat being handed the
///      console player's pad by elimination while the panel reported a healthy verified jail.
///
/// Off by default (<see cref="MultiSeatOptions.EnableHidHideCloaking"/>). A host that sets
/// nothing behaves exactly as before.
/// </summary>
public sealed class HidHideConfigurator
{
    private readonly ILogger<HidHideConfigurator> _logger;
    private readonly MultiSeatOptions _options;
    private readonly HidHideCli _cli;

    // What we confined, so teardown can release exactly that and nothing else.
    private readonly ConcurrentDictionary<Guid, SeatJailState> _seatStates = new();

    public HidHideConfigurator(ILogger<HidHideConfigurator> logger, IOptions<MultiSeatOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        _cli = new HidHideCli(logger, _options.HidHideCliPath);

        if (!_cli.IsAvailable)
        {
            _logger.LogWarning(
                "HidHide CLI not found at {Path}. Per-seat gamepad isolation unavailable. " +
                "Install HidHide from https://github.com/nefarius/HidHide/releases",
                _options.HidHideCliPath);
        }
    }

    public bool IsDriverAvailable => _cli.IsAvailable;

    /// <summary>
    /// True when the feature is both switched on and able to run.
    /// </summary>
    public bool IsEnabled => _options.EnableHidHideCloaking && _cli.IsAvailable;

    // ═══════════════════════════════════════════════════════════════════
    //  STARTUP
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Clear jail rules this service left behind, and only those.
    ///
    /// HidHide's blacklist is kernel-side and persists across reboots, while our in-memory state
    /// does not — so without this a crash would strand a pad confined to a session that no longer
    /// exists, and it would stay that way forever.
    ///
    /// ⚠️ Only entries carrying a session suffix are removed. A plain, unsuffixed hide is
    /// something a user (or HidHide's own GUI) put there by hand, and this is precisely the
    /// situation where an empty read must not be mistaken for an empty configuration.
    /// </summary>
    public void ResetOnStartup()
    {
        if (!_cli.IsAvailable) return;

        try
        {
            var read = _cli.Read("--dev-list");
            if (!read.Answered)
            {
                _logger.LogWarning(
                    "HidHide startup reset skipped: the CLI did not answer, so its configuration is " +
                    "unknown. Removing nothing is the safe reading — an empty answer from this tool " +
                    "is indistinguishable from an empty blacklist.");
                return;
            }

            var jailed = HidHideDeviceParser.ParseHiddenDevices(read.Output)
                .Where(entry => HidHideSessionJail.Split(entry).SessionId is not null)
                .ToList();

            if (jailed.Count == 0)
            {
                _logger.LogInformation("HidHide startup reset: no session-jail rules left behind.");
                return;
            }

            var release = HidHideCli.Sequence(jailed.Select(HidHideConfigurator.UnhideDeviceArgs).ToArray());
            var result = _cli.Write(release);

            _logger.LogInformation(
                "HidHide startup reset: released {Count} stale session-jail rule(s) [{Rules}]{Failed}",
                jailed.Count, string.Join(", ", jailed), result.Succeeded ? "" : " — the CLI reported a failure");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HidHide startup reset failed (non-critical)");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  READS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Gaming devices HidHide can see, absent nodes dropped.
    /// </summary>
    public List<HidHideDevice> ListGamingDevices()
    {
        if (!_cli.IsAvailable) return [];

        var read = _cli.Read(ListGamingDevicesCommand);
        if (!read.Answered)
        {
            _logger.LogWarning(
                "HidHide did not answer a device listing. Treating this as a FAILED READ, not as " +
                "'no gamepads' — those two look identical from this tool and only one of them is " +
                "safe to act on.");
            return [];
        }

        return HidHideDeviceParser.Parse(read.Output);
    }

    /// <summary>
    /// Applications allowed to see hidden devices.
    ///
    /// ⚠️ Under confinement this list must stay empty. It is global and cannot pair an app with a
    /// device, so a single entry sees EVERY confined pad — his had twelve, and a game in one seat
    /// was moved by both players' controllers.
    /// </summary>
    public List<string> ListWhitelistedApps()
    {
        if (!_cli.IsAvailable) return [];

        var read = _cli.Read("--app-list");
        return read.Answered ? HidHideDeviceParser.ParseAppList(read.Output) : [];
    }

    /// <summary>
    /// Whitelist entries that would defeat confinement, i.e. everything except HidHide's own
    /// binaries.
    ///
    /// HidHide whitelists its own CLI and GUI and <c>--app-unreg</c> on them does not stick, so a
    /// check for "the list is empty" reports a problem on every host that has ever installed
    /// HidHide — which is how @jmlopezdona's first version of this warning came to read
    /// "not guaranteed" everywhere and was therefore ignored on the day it mattered.
    /// </summary>
    public List<string> ForeignWhitelistEntries() =>
        ListWhitelistedApps()
            .Where(app => !app.Contains(@"\HidHide\", StringComparison.OrdinalIgnoreCase))
            .ToList();

    // ═══════════════════════════════════════════════════════════════════
    //  PRE-WRITE — rules before the pad exists
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Write a seat's jail rules before its Apollo creates a pad.
    ///
    /// This is correctness, not optimisation. HidHide filters at open time and a pass costs
    /// seconds: measured, a pad appeared at 13:27:33 and its rule landed at 13:27:39.9. Inside
    /// that window <c>dwm</c>, <c>explorer</c> and <c>GameInputSvc</c> of every session open the
    /// new pad and keep handles that never expire — and releasing a wrong rule afterwards does
    /// NOT hand the pad back, because the real owner tried once and does not retry.
    ///
    /// A rule for an absent device matches nothing, so writing one in advance is inert and free.
    ///
    /// ⚠️ It can only pre-write paths whose ownership was established by EVIDENCE. Paths guessed
    /// by elimination are deliberately not remembered: pre-writing one hands another player's pad
    /// to this seat before anything has had a chance to look at it.
    /// </summary>
    public void PreWriteRules(SeatInfo seat)
    {
        if (!IsEnabled || !_options.EnablePadRulePreWrite) return;
        if (seat.SessionId <= 0) return;

        var known = _options.SeatPadDevicePaths.TryGetValue(seat.AccountName, out var paths) ? paths : [];
        if (known.Length == 0)
        {
            _logger.LogInformation(
                "Seat {Id}: nothing to pre-write — no pad path is known for account {Account} by " +
                "evidence. The reactive path will confine its pad after creation, which leaves the " +
                "open-time window that pre-writing exists to close. Configure " +
                "MultiSeat:SeatPadDevicePaths:{Account} once the seat's XUSB path is known.",
                seat.Id, seat.AccountName, seat.AccountName);
            return;
        }

        var rules = known.Select(p => HidHideSessionJail.Confine(p, seat.SessionId)).ToList();
        var result = _cli.Write(HidHideCli.Sequence(rules.Select(HideDeviceArgs).ToArray()));

        if (result.Succeeded)
        {
            var state = _seatStates.GetOrAdd(seat.Id, _ => new SeatJailState());
            foreach (var rule in rules) state.Rules.Add(rule);

            _logger.LogInformation(
                "Seat {Id}: pre-wrote {Count} jail rule(s) for session {Session} [{Rules}]",
                seat.Id, rules.Count, seat.SessionId, string.Join(", ", rules));
        }
        else
        {
            _logger.LogWarning("Seat {Id}: pre-writing jail rules failed; falling back to the reactive path", seat.Id);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CONFINE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Confine this seat's pad to this seat's session.
    /// </summary>
    public void CloakForSession(SeatInfo seat)
    {
        if (!_options.EnableHidHideCloaking)
        {
            _logger.LogInformation(
                "Seat {Id}: per-seat gamepad isolation is off (MultiSeat:EnableHidHideCloaking). " +
                "Pads created by any Apollo instance are visible to every session on this host.",
                seat.Id);
            return;
        }

        if (!_cli.IsAvailable)
        {
            _logger.LogWarning("Seat {Id}: gamepad isolation is on but HidHide is not installed — nothing is isolated", seat.Id);
            return;
        }

        if (seat.SessionId <= 0)
        {
            _logger.LogWarning(
                "Seat {Id}: no session id yet, so no jail rule can be written. The driver requires " +
                "sessionId != 0; a rule for session 0 hides the pad everywhere instead of confining it.",
                seat.Id);
            return;
        }

        try
        {
            WarnAboutForeignWhitelistEntries(seat);

            var pad = AttributePadTo(seat);
            if (pad is null) return;

            var rules = HidHideSessionJail.ConfineAll(pad, seat.SessionId);
            var result = _cli.Write(HidHideCli.Sequence(rules.Select(HideDeviceArgs).ToArray()));

            if (!result.Succeeded)
            {
                _logger.LogWarning("Seat {Id}: writing jail rules failed — the pad is NOT confined", seat.Id);
                return;
            }

            // Cloaking has to be on for any of it to take effect.
            _cli.Write("--cloak-on");

            var state = _seatStates.GetOrAdd(seat.Id, _ => new SeatJailState());
            foreach (var rule in rules) state.Rules.Add(rule);
            state.SymbolicLink = pad.SymbolicLink;

            _logger.LogInformation(
                "Seat {Id}: confined '{Pad}' to session {Session} with {Count} rule(s) [{Rules}]",
                seat.Id, pad.FriendlyName, seat.SessionId, rules.Count, string.Join(", ", rules));

            VerifyJail(seat, pad);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Seat {Id}: failed to confine a gamepad", seat.Id);
        }
    }

    /// <summary>
    /// Release the rules written for this seat, and nothing else.
    /// </summary>
    public void UncloakForSession(SeatInfo seat)
    {
        if (!_cli.IsAvailable) return;
        if (!_seatStates.TryRemove(seat.Id, out var state) || state.Rules.Count == 0) return;

        try
        {
            var result = _cli.Write(HidHideCli.Sequence(state.Rules.Select(UnhideDeviceArgs).ToArray()));

            if (_seatStates.IsEmpty)
            {
                _cli.Write("--cloak-off");
                _logger.LogInformation(
                    "Seat {Id}: released {Count} jail rule(s), cloaking off — no confined seats remain",
                    seat.Id, state.Rules.Count);
            }
            else
            {
                _logger.LogInformation(
                    "Seat {Id}: released {Count} jail rule(s); {Remaining} confined seat(s) remain",
                    seat.Id, state.Rules.Count, _seatStates.Count);
            }

            if (!result.Succeeded)
                _logger.LogWarning("Seat {Id}: the CLI reported a failure while releasing jail rules", seat.Id);

            // ⚠️ Releasing a rule does not hand the pad back to whoever should have had it. HidHide
            // filters at open time: the session that already opened the device keeps its handle,
            // and the rightful owner tried once and does not retry. Measured recovery took two
            // reconnections. Worth saying plainly rather than implying teardown restored anything.
            _logger.LogInformation(
                "Seat {Id}: a pad opened under the released rule stays open in whichever session " +
                "grabbed it — reconnecting the client is what re-creates the pad.",
                seat.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Seat {Id}: failed to release jail rules", seat.Id);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PROBE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Check from session 0 that the rule actually took.
    ///
    /// The driver requires <c>sessionId != 0</c> for a jail to match, so this service — which
    /// runs in session 0 and is therefore never the jailed session — must be refused a device
    /// that is genuinely confined. That makes "can I still open it?" a live check on an
    /// undocumented behaviour that a future HidHide release could drop silently.
    ///
    /// ⚠️ What it proves is one-directional: a refusal here means session 0 is being denied. It
    /// does not prove the seat can still see the pad — a plain global hide, or a rule whose suffix
    /// was stripped, would look exactly the same from here.
    ///
    /// ⚠️ And it is the KIND of failure that answers, not the fact of one. A jail refuses with
    /// <c>ERROR_ACCESS_DENIED</c>; an absent device fails with <c>ERROR_FILE_NOT_FOUND</c>. Only
    /// the first is evidence — see <see cref="HidHideSessionJail.Probe"/>.
    /// </summary>
    private void VerifyJail(SeatInfo seat, HidHideDevice pad)
    {
        if (!_options.VerifyHidHideJail) return;
        if (string.IsNullOrWhiteSpace(pad.SymbolicLink)) return;

        var probe = HidHideSessionJail.Probe(pad.SymbolicLink);

        switch (probe.Verdict)
        {
            case HidHideSessionJail.JailProbe.Open:
                _logger.LogWarning(
                    "Seat {Id}: jail rules were written but session 0 can still open '{Pad}'. The " +
                    "confinement is NOT in effect — either cloaking is off, an application whitelist " +
                    "entry is defeating it, or this HidHide build no longer honours the '!<session>' " +
                    "suffix (it is undocumented). Gamepad input is not isolated.",
                    seat.Id, pad.FriendlyName);
                break;

            case HidHideSessionJail.JailProbe.Confined:
                _logger.LogInformation(
                    "Seat {Id}: verified — session 0 is refused '{Pad}', which is what a live jail " +
                    "looks like from outside it.",
                    seat.Id, pad.FriendlyName);
                break;

            default:
                // NOT a success. Nobody can open a device that is not there, so this says nothing
                // about the rule — and reporting it as a working jail is how a rule that matches
                // nothing gets recorded as one being enforced.
                _logger.LogWarning(
                    "Seat {Id}: could not verify the jail on '{Pad}' — opening it failed with " +
                    "Win32 error {Error}, which is not a refusal (a jail gives {Denied}). The " +
                    "device is most likely gone, so the rules that were written may match nothing. " +
                    "Nothing was proved either way.",
                    seat.Id, pad.FriendlyName, probe.Error, HidHideSessionJail.ERROR_ACCESS_DENIED);
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ATTRIBUTION — identity first, evidence second, elimination last
    // ═══════════════════════════════════════════════════════════════════

    private HidHideDevice? AttributePadTo(SeatInfo seat)
    {
        var devices = ListGamingDevices();
        if (devices.Count == 0)
        {
            _logger.LogInformation(
                "Seat {Id}: HidHide reports no present gaming devices, so there is nothing to " +
                "confine yet. Apollo creates its pad when a Moonlight client connects, not at " +
                "seat provision.",
                seat.Id);
            return null;
        }

        // 1. IDENTITY. A configured path for this account is the only non-guess we have; the
        //    alternative is a patched Apollo that pins each seat's pad serial, which is deferred.
        if (_options.SeatPadDevicePaths.TryGetValue(seat.AccountName, out var known) && known.Length > 0)
        {
            var byIdentity = devices.FirstOrDefault(d =>
                known.Any(k => k.Equals(d.DeviceInstancePath, StringComparison.OrdinalIgnoreCase) ||
                               k.Equals(d.BaseContainerDeviceInstancePath, StringComparison.OrdinalIgnoreCase)));

            if (byIdentity is not null)
            {
                _logger.LogInformation("Seat {Id}: pad identified by configured path for {Account}", seat.Id, seat.AccountName);
                return byIdentity;
            }
        }

        // 2. Only emulated pads are ours to confine. A physical controller belongs to whoever is
        //    holding it, and confining one to a seat takes it away from them.
        var emulated = devices.Where(HidHideSessionJail.IsEmulatedPad).ToList();

        if (emulated.Count == 0)
        {
            _logger.LogInformation(
                "Seat {Id}: {Total} gaming device(s) present, none of them emulated. Physical " +
                "controllers are left alone — confining one would take it from the person holding it.",
                seat.Id, devices.Count);
            return null;
        }

        // Anything already carrying a jail rule belongs to some session; don't steal it.
        var claimed = ClaimedPaths();
        var free = emulated.Where(d => !d.Nodes.Any(n => claimed.Contains(n))).ToList();

        if (free.Count == 0)
        {
            _logger.LogWarning(
                "Seat {Id}: every emulated pad is already confined to another session. Nothing " +
                "was changed — taking one would leave that session without input.",
                seat.Id);
            return null;
        }

        // 3. ELIMINATION, and only when it is unambiguous.
        if (free.Count > 1)
        {
            _logger.LogWarning(
                "Seat {Id}: {Count} unconfined emulated pads and no way to tell which belongs to " +
                "this seat [{Paths}]. Nothing was confined, deliberately. Guessing here is how a " +
                "seat ends up holding the console player's controller while the dashboard reports " +
                "a healthy jail. Set MultiSeat:SeatPadDevicePaths:{Account} to resolve it.",
                seat.Id, free.Count, string.Join(", ", free.Select(d => d.BaseContainerDeviceInstancePath)), seat.AccountName);
            return null;
        }

        var chosen = free[0];
        _logger.LogWarning(
            "Seat {Id}: pad '{Pad}' ({Path}) attributed to this seat BY ELIMINATION — it is the " +
            "only unconfined emulated pad, not evidence that it is this seat's. If a console-side " +
            "Apollo is running, its pad is indistinguishable from a seat's and may be the one " +
            "taken. This decision is not remembered and is re-derived every time.",
            seat.Id, chosen.FriendlyName, chosen.BaseContainerDeviceInstancePath);

        return chosen;
    }

    private HashSet<string> ClaimedPaths()
    {
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var read = _cli.Read("--dev-list");
        if (!read.Answered) return claimed;

        foreach (var entry in HidHideDeviceParser.ParseHiddenDevices(read.Output))
        {
            var (path, session) = HidHideSessionJail.Split(entry);
            if (session is not null) claimed.Add(path);
        }

        return claimed;
    }

    private void WarnAboutForeignWhitelistEntries(SeatInfo seat)
    {
        var foreign = ForeignWhitelistEntries();
        if (foreign.Count == 0) return;

        _logger.LogWarning(
            "Seat {Id}: {Count} application(s) are whitelisted in HidHide and each of them can see " +
            "EVERY confined pad, which defeats per-seat isolation entirely: {Apps}. The whitelist is " +
            "global and cannot pair an application with a device. Note it protects the process that " +
            "OPENS the device, which is often not the game — Steam titles frequently read pads " +
            "through steam.exe.",
            seat.Id, foreign.Count, string.Join(", ", foreign));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  WHITELIST (kept for explicit operator use)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Whitelist an application. ⚠️ Under confinement this hands that application every confined
    /// pad on the host, so it is logged loudly rather than quietly obeyed.
    /// </summary>
    public void WhitelistApplication(string exePath)
    {
        if (!_cli.IsAvailable) return;

        _cli.Write(RegisterAppArgs(exePath));

        if (_options.EnableHidHideCloaking)
        {
            _logger.LogWarning(
                "Whitelisted {Path} in HidHide. While per-seat isolation is on this application can " +
                "see every confined pad on the host, in every seat.",
                exePath);
        }
        else
        {
            _logger.LogInformation("Whitelisted application: {Path}", exePath);
        }
    }

    public void UnwhitelistApplication(string exePath)
    {
        if (!_cli.IsAvailable) return;

        _cli.Write(UnregisterAppArgs(exePath));
        _logger.LogInformation("Unwhitelisted application: {Path}", exePath);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ARGUMENT CONSTRUCTION
    // ═══════════════════════════════════════════════════════════════════
    // Centralised because these were wrong at ELEVEN call sites and nothing noticed. Every switch
    // takes its value directly, quoted; there is no --id and no --path form. Guarded by
    // HidHideArgumentTests, which asserts those forms can never come back.

    internal static string HideDeviceArgs(string deviceInstancePath) =>
        $"--dev-hide \"{deviceInstancePath}\"";

    internal static string UnhideDeviceArgs(string deviceInstancePath) =>
        $"--dev-unhide \"{deviceInstancePath}\"";

    internal static string RegisterAppArgs(string exePath) =>
        $"--app-reg \"{exePath}\"";

    internal static string UnregisterAppArgs(string exePath) =>
        $"--app-unreg \"{exePath}\"";

    internal const string ListGamingDevicesCommand = "--dev-gaming";

    // Read-only queries must carry --cancel: HidHideCLI saves its configuration on exit even for a
    // pure listing, so a bare --dev-gaming rewrites the config it was asked to report on. Reads go
    // through HidHideCli.Read, which adds it (and --cloak-state) to every listing.
    internal static string ListGamingDevicesArgs => HidHideCli.Sequence("--cloak-state", ListGamingDevicesCommand, "--cancel");
    internal static string ListAppsArgs => HidHideCli.Sequence("--cloak-state", "--app-list", "--cancel");

    /// <summary>
    /// The jail rules written for one seat, so teardown releases exactly those.
    /// </summary>
    private sealed class SeatJailState
    {
        public HashSet<string> Rules { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string SymbolicLink { get; set; } = "";
    }
}
