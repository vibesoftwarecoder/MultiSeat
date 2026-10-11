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
        std::cout << "Steam command-line and environment policy tests passed\n"; return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
