using System.Reflection;
using System.Runtime.InteropServices;

namespace MultiSeat.Service.SteamShim;

/// <summary>What the shim installer reads from and writes back to a <c>.lnk</c> file.</summary>
internal sealed record ShortcutInfo(
    string Target, string Arguments, string WorkingDirectory, string IconLocation, string Description);

/// <summary>A shortcut file, so the installer is tested without touching a real Start Menu.</summary>
internal interface IShortcutStore
{
    /// <summary>The shortcut at <paramref name="path"/>, or null when there is no such file.</summary>
    ShortcutInfo? Read(string path);

    void Write(string path, ShortcutInfo info);
}

/// <summary>
/// Shortcuts through <c>WScript.Shell</c>, called late-bound so the service needs no COM interop
/// assembly. Windows' own shortcut object keeps everything the installer does not change (window
/// style, hotkey, the AppUserModelID on pinned shortcuts), because it edits the file in place.
/// </summary>
internal sealed class WScriptShortcutStore : IShortcutStore
{
    public ShortcutInfo? Read(string path)
    {
        if (!File.Exists(path)) return null;
        return WithShortcut(path, link => new ShortcutInfo(
            Text(link, "TargetPath"), Text(link, "Arguments"), Text(link, "WorkingDirectory"),
            Text(link, "IconLocation"), Text(link, "Description")));
    }

    public void Write(string path, ShortcutInfo info) =>
        WithShortcut(path, link =>
        {
            Put(link, "TargetPath", info.Target);
            Put(link, "Arguments", info.Arguments);
            Put(link, "WorkingDirectory", info.WorkingDirectory);
            Put(link, "IconLocation", info.IconLocation);
            Put(link, "Description", info.Description);
            link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            return true;
        });

    private static T WithShortcut<T>(string path, Func<object, T> use)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is not available.");
        var shell = Activator.CreateInstance(type)!;
        object? link = null;
        try
        {
            link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path])!;
            return use(link);
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static string Text(object link, string name) =>
        link.GetType().InvokeMember(name, BindingFlags.GetProperty, null, link, null) as string ?? "";

    private static void Put(object link, string name, string value) =>
        link.GetType().InvokeMember(name, BindingFlags.SetProperty, null, link, [value]);
}
