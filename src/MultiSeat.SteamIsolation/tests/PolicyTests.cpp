#include "../IsolationPolicy.h"
#include <iostream>
#include <stdexcept>
using namespace steam_isolation;
static void Expect(bool result, const char* message) { if (!result) throw std::runtime_error(message); }
static std::vector<std::wstring> Args(const std::wstring& command) {
    int count = 0; LPWSTR* parsed = CommandLineToArgvW(command.c_str(), &count);
    if (!parsed) throw std::runtime_error("Cannot parse command");
    std::vector<std::wstring> result(parsed, parsed + count); LocalFree(parsed); return result;
}
int main() {
    try {
        const std::wstring key = L"MultiSeat_17_123";
        auto input = L"\"C:\\Program Files (x86)\\Steam\\steam.exe\" -bigpicture \"a\\\"b\" \"trailing\\\\\"";
        auto before = Args(input), after = Args(RewriteCommand(input, key));
        Expect(after.size() == before.size() + 2, "IPC option missing");
        for (size_t i = 0; i < before.size(); ++i) Expect(before[i] == after[i], "Argument semantics changed");
        Expect(after.back() == key, "Wrong namespace");
        after = Args(RewriteCommand(L"steam.exe -master_ipc_name_override wrong -bigpicture", key));
        Expect(after.size() == 4 && after[1] == L"-bigpicture" && after[3] == key, "IPC duplicate not replaced");
        after = Args(RewriteCommand(L"steam.exe -master_ipc_name_override=wrong", key));
        Expect(after.size() == 3 && after[2] == key, "Inline IPC duplicate not replaced");
        const std::wstring browser = L"steamwebhelper.exe -cachedir=\"C:\\cache folder\\htmlcache\" --log-file=C:\\logs\\cef_log.txt --other=x";
        auto changed = RewriteCommand(browser, key); after = Args(changed);
        Expect(after[1] == L"-cachedir=C:\\cache folder\\htmlcache." + key, "Browser cache not separated");
        Expect(after[2] == L"--log-file=C:\\logs\\cef_log." + key + L".txt", "Browser log not separated");
        Expect(RewriteCommand(changed, key) == changed, "Browser suffix added twice");
        Expect(RewriteCommand(L"mySteam.exe -cachedir=C:\\htmlcache", key) == L"mySteam.exe -cachedir=C:\\htmlcache", "Unrelated program changed");
        Expect(RewriteCommand(L"steamwebhelper.exe -cachedir=C:\\myhtmlcache", key) == L"steamwebhelper.exe -cachedir=C:\\myhtmlcache", "Substring changed");
        after = Args(RewriteCommand(L"steam.exe https://example.test/htmlcache --label=cef_log", key));
        Expect(after[1] == L"https://example.test/htmlcache" && after[2] == L"--label=cef_log", "URL or label changed");
        // An unquoted path that contains spaces. CommandLineToArgvW reads its first word as
        // "C:\Program", so before SplitImage such a command was neither rewritten nor isolated.
        const std::wstring spaced = L"C:\\Program Files (x86)\\Steam\\steam.exe";
        auto spacedExists = [](const std::wstring& path) { return path == L"C:\\Program Files (x86)\\Steam\\steam.exe"; };
        auto neverExists = [](const std::wstring&) { return false; };
        const std::wstring unquoted = spaced + L" -silent \"two words\"";
        auto rewritten = Args(RewriteCommand(unquoted, key, spacedExists));
        Expect(rewritten.size() == 5, "Unquoted spaced path: argument count wrong");
        Expect(rewritten[0] == spaced && rewritten[1] == L"-silent" && rewritten[2] == L"two words", "Unquoted spaced path: arguments changed");
        Expect(rewritten[3] == L"-master_ipc_name_override" && rewritten[4] == key, "Unquoted spaced path: IPC option missing");
        Expect(RewriteCommand(unquoted, key, neverExists) == unquoted, "A folder that does not exist was taken for Steam");
        const std::wstring spacedBrowser = L"C:\\Program Files (x86)\\Steam\\bin\\cef\\steamwebhelper.exe";
        auto browserExists = [](const std::wstring& path) { return path == L"C:\\Program Files (x86)\\Steam\\bin\\cef\\steamwebhelper.exe"; };
        auto browserArgs = Args(RewriteCommand(spacedBrowser + L" -cachedir=C:\\cache\\htmlcache", key, browserExists));
        Expect(browserArgs[0] == spacedBrowser && browserArgs[1] == L"-cachedir=C:\\cache\\htmlcache." + key, "Unquoted spaced browser path not isolated");
        Expect(RewriteCommand(L"C:\\Tools\\runner.exe " + spaced, key, spacedExists) == L"C:\\Tools\\runner.exe " + spaced,
               "A program that merely has Steam in its arguments was taken for Steam");
        // The first word is taken on its name alone: "steam.exe -silent" is found through the search path.
        Expect(Args(RewriteCommand(L"steam.exe -silent", key, neverExists)).size() == 4, "Bare steam.exe needs no file on disk");

        auto split = SplitImage(L"  \"C:\\Program Files\\x\\steam.exe\" -a -b", neverExists);
        Expect(split.image == L"C:\\Program Files\\x\\steam.exe" && split.rest == L" -a -b", "Quoted split wrong");
        split = SplitImage(L"steam.exe -a", neverExists);
        Expect(split.image == L"steam.exe" && split.rest == L" -a", "First-word split wrong");
        split = SplitImage(spaced + L" -a", spacedExists);
        Expect(split.image == spaced && split.rest == L" -a", "Spaced split wrong");
        split = SplitImage(L"other.exe -a", neverExists);
        Expect(split.image == L"other.exe" && split.rest == L" -a", "Other programs come back as their first word");
        Expect(SplitImage(L"   ", neverExists).image.empty() && SplitImage(L"", neverExists).image.empty(), "Blank command not empty");
        split = SplitImage(L"\"C:\\unclosed\\steam.exe -a", neverExists);
        Expect(split.image == L"C:\\unclosed\\steam.exe -a" && split.rest.empty(), "Unclosed quote handled wrong");

        const wchar_t environment[] = L"=C:=C:\\work\0HOME=C:\\profile\0StEaM_MaStEr_IpC_NaMe_OvErRiDe=old\0TEST=ok\0";
        auto block = RewriteEnvironment(environment, sizeof(environment) / sizeof(wchar_t), key);
        Expect(!block.empty() && block.back() == 0 && block[block.size() - 2] == 0, "Environment terminator missing");
        int found = 0; bool drive = false, test = false;
        for (const wchar_t* p = block.data(); *p; p += wcslen(p) + 1) {
            std::wstring value(p);
            if (value == L"steam_master_ipc_name_override=" + key) ++found;
            if (value == L"=C:=C:\\work") drive = true;
            if (value == L"TEST=ok") test = true;
            Expect(value.find(L"=old") == std::wstring::npos, "Old IPC retained");
        }
        Expect(found == 1 && drive && test, "Environment unrelated entries lost");
        Expect(RewriteEnvironment(L"broken", 6, key).empty(), "Malformed block accepted");
        // A buffer larger than the block, with leftover text after the terminator (seen live with a game launch).
        const wchar_t padded[] = L"A=1\0B=2\0\0stale\0words12";
        auto trimmed = RewriteEnvironment(padded, sizeof(padded) / sizeof(wchar_t), key);
        int paddedEntries = 0; bool leaked = false;
        for (const wchar_t* p = trimmed.data(); !trimmed.empty() && *p; p += wcslen(p) + 1) { ++paddedEntries; if (!wcsncmp(p, L"stale", 5)) leaked = true; }
        Expect(paddedEntries == 3 && !leaked, "Padding after the environment terminator was read as entries");
        // Steam's machine-wide key, in both registry views, and nothing that merely resembles it.
        Expect(IsMachineSteamKeyPath(L"\\REGISTRY\\MACHINE\\SOFTWARE\\WOW6432Node\\Valve\\Steam"), "32-bit view not recognised");
        Expect(IsMachineSteamKeyPath(L"\\REGISTRY\\MACHINE\\SOFTWARE\\Valve\\Steam"), "64-bit view not recognised");
        Expect(IsMachineSteamKeyPath(L"\\registry\\machine\\software\\valve\\steam"), "Case mattered");
        Expect(!IsMachineSteamKeyPath(L"\\REGISTRY\\USER\\S-1-5-21-1\\Software\\Valve\\Steam"), "A user key was taken for the machine key");
        Expect(!IsMachineSteamKeyPath(L"\\REGISTRY\\MACHINE\\SOFTWARE\\Valve\\Steam\\Apps"), "A subkey was taken for the machine key");
        Expect(!IsMachineSteamKeyPath(L"\\REGISTRY\\MACHINE\\SOFTWARE\\Valve\\SteamService"), "A look-alike was taken for the machine key");
        Expect(!IsMachineSteamKeyPath(L"\\REGISTRY\\USER\\S-1-5-21-1\\Software\\MultiSeat\\SteamIsolationTests\\5"), "The fixture key was taken for Steam's");
        Expect(!IsMachineSteamKeyPath(L""), "Empty path accepted");
        std::cout << "Steam command-line and environment policy tests passed\n"; return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
