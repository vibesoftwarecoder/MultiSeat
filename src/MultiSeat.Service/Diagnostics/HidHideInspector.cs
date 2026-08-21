using Microsoft.Extensions.Logging.Abstractions;
using MultiSeat.Service.Input;

namespace MultiSeat.Service.Diagnostics;

/// <summary>
/// Reports what HidHide actually sees and what it would do, on the host it is run on.
///
/// This exists because every single thing this feature depends on has been wrong at some point
/// while reporting success: invented CLI arguments, a parser that matched nothing, a device list
/// that was silently empty, and an "emulated" test that read every emulated pad as physical. The
/// answer to all of them is the same — ask the machine rather than reason about it.
///
///   MultiSeat.Service.exe --hidhide
///
/// Exits 0 when per-seat isolation could work here, 1 when something in the chain is broken or
/// missing. Read-only: every CLI call carries --cancel.
/// </summary>
public static class HidHideInspector
{
    public static int Report(string cliPath)
    {
        var cli = new HidHideCli(NullLogger.Instance, cliPath);

        Console.WriteLine("HidHide inspection");
        Console.WriteLine("==================");
        Console.WriteLine($"CLI path : {cliPath}");

        if (!cli.IsAvailable)
        {
            Console.WriteLine("CLI      : NOT FOUND — install HidHide from https://github.com/nefarius/HidHide/releases");
            return 1;
        }

        Console.WriteLine("CLI      : present");
        Console.WriteLine();

        // ── Does it answer at all? ────────────────────────────────────
        // An empty read from this tool is indistinguishable from an empty configuration, so the
        // cloak-state line is the only thing that says the invocation was real.
        var state = cli.Read("--dev-list --app-list");
        if (!state.Answered)
        {
            Console.WriteLine("The CLI did not answer (no cloak state in its output).");
            Console.WriteLine("That is a FAILED READ, not an empty configuration — do not act on it.");
            Console.WriteLine(state.TimedOut ? "It timed out." : $"Exit code {state.ExitCode}.");
            return 1;
        }

        var cloakOn = HidHideDeviceParser.ParseCloakState(state.Output) ?? false;
        Console.WriteLine($"Cloaking : {(cloakOn ? "ON" : "off")}");

        // ── Whitelist ─────────────────────────────────────────────────
        var apps = HidHideDeviceParser.ParseAppList(state.Output);
        var foreign = apps.Where(a => !a.Contains(@"\HidHide\", StringComparison.OrdinalIgnoreCase)).ToList();

        Console.WriteLine($"Whitelist: {apps.Count} entr(y/ies), {foreign.Count} of them foreign");
        foreach (var app in apps)
        {
            var own = !foreign.Contains(app);
            Console.WriteLine($"           {(own ? "[hidhide]" : "[FOREIGN]")} {app}");
        }
        if (foreign.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  ⚠ Each foreign entry can see EVERY confined pad, in every session, which");
            Console.WriteLine("    defeats per-seat isolation. The list is global and cannot pair an app with");
            Console.WriteLine("    a device. Note it protects the process that OPENS the pad, often not the");
            Console.WriteLine("    game itself — Steam titles frequently read pads through steam.exe.");
        }

        // ── Existing rules ────────────────────────────────────────────
        var hidden = HidHideDeviceParser.ParseHiddenDevices(state.Output);
        Console.WriteLine();
        Console.WriteLine($"Blacklist: {hidden.Count} rule(s)");
        foreach (var entry in hidden)
        {
            var (path, session) = HidHideSessionJail.Split(entry);
            Console.WriteLine(session is null
                ? $"           [global ] {path}   <- hides it from everyone, including seats"
                : $"           [session {session}] {path}");
        }

        // ── Devices ───────────────────────────────────────────────────
        var devices = cli.Read("--dev-gaming");
        if (!devices.Answered)
        {
            Console.WriteLine();
            Console.WriteLine("Device listing FAILED to answer. Not the same as 'no gamepads'.");
            return 1;
        }

        var pads = HidHideDeviceParser.Parse(devices.Output);
        Console.WriteLine();
        Console.WriteLine($"Gaming devices present: {pads.Count}");

        foreach (var pad in pads)
        {
            // Both parents, deliberately. The HID node's parent is the USB composite interface and
            // looks physical even for a ViGEm pad; only the XUSB node's parent shows ROOT\VIGEMBUS.
            // Printing one parent next to a verdict derived from the other is how a diagnostic
            // starts lying quietly.
            var hidParent = HidHideSessionJail.GetParentInstanceId(pad.DeviceInstancePath);
            var xusbParent = HidHideSessionJail.GetParentInstanceId(pad.BaseContainerDeviceInstancePath);
            var emulated = HidHideSessionJail.IsEmulatedPad(pad);

            Console.WriteLine();
            Console.WriteLine($"  {pad.FriendlyName}");
            Console.WriteLine($"    HID node    : {pad.DeviceInstancePath}");
            Console.WriteLine($"    XUSB node   : {pad.BaseContainerDeviceInstancePath}   <- the one XInput reads");
            Console.WriteLine($"    HID parent  : {hidParent ?? "<unknown>"}");
            Console.WriteLine($"    XUSB parent : {xusbParent ?? "<unknown>"}   <- ROOT\\... here means emulated");
            Console.WriteLine($"    verdict     : {(emulated ? "EMULATED (ours to confine)" : "physical (left alone)")}");
            var probe = HidHideSessionJail.Probe(pad.SymbolicLink);
            Console.WriteLine($"    session 0 probe: {probe.Verdict}" +
                              (probe.Error == 0 ? "" : $" (win32 {probe.Error})") +
                              probe.Verdict switch
                              {
                                  HidHideSessionJail.JailProbe.Open => "   <- openable here, so no jail is in effect",
                                  HidHideSessionJail.JailProbe.Confined => "   <- refused, which is what a live jail looks like",
                                  _ => "   <- not a refusal, so nothing is proved either way",
                              });
            Console.WriteLine($"    rules a jail to session N would write:");
            foreach (var rule in HidHideSessionJail.ConfineAll(pad, 99))
                Console.WriteLine($"       --dev-hide \"{rule.Replace("!99", "!<N>")}\"");
        }

        // ── Verdict ───────────────────────────────────────────────────
        Console.WriteLine();
        var emulatedPads = pads.Count(HidHideSessionJail.IsEmulatedPad);

        if (foreign.Count > 0)
        {
            Console.WriteLine("VERDICT: confinement would be DEFEATED by the foreign whitelist entries above.");
            return 1;
        }

        Console.WriteLine(emulatedPads > 1
            ? $"VERDICT: usable, but {emulatedPads} emulated pads are present — attribution by elimination " +
              "is refused when more than one is unconfined. Set MultiSeat:SeatPadDevicePaths."
            : "VERDICT: usable.");

        return 0;
    }
}
