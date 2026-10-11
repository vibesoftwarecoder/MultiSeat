#include "Inject.h"
#include <iostream>

using namespace steam_isolation;

// Decode only the launcher prefix. The application's argument tail remains byte-for-byte intact.
static bool NextToken(const std::wstring& raw, size_t& cursor, std::wstring& token) {
    token.clear();
    while (cursor < raw.size() && (raw[cursor] == L' ' || raw[cursor] == L'\t')) ++cursor;
    if (cursor == raw.size()) return false;
    bool quoted = false;
    while (cursor < raw.size()) {
        if (!quoted && (raw[cursor] == L' ' || raw[cursor] == L'\t')) break;
        size_t slashes = 0;
        while (cursor < raw.size() && raw[cursor] == L'\\') { ++slashes; ++cursor; }
        if (cursor < raw.size() && raw[cursor] == L'"') {
            token.append(slashes / 2, L'\\');
            if (slashes % 2) { token.push_back(L'"'); ++cursor; }
            else {
                ++cursor;
                if (quoted && cursor < raw.size() && raw[cursor] == L'"') { token.push_back(L'"'); ++cursor; }
                else quoted = !quoted;
            }
        } else {
            token.append(slashes, L'\\');
            if (cursor < raw.size() && (quoted || (raw[cursor] != L' ' && raw[cursor] != L'\t')))
                token.push_back(raw[cursor++]);
        }
    }
    return !quoted;
}
static DWORD Run(const std::wstring& executable, std::wstring command, const std::wstring& hook,
                 const std::wstring& workingDirectory, HANDLE readyEvent = nullptr) {
    Handle job(CreateJobObjectW(nullptr, nullptr));
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!job.value || !SetInformationJobObject(job.value, JobObjectExtendedLimitInformation, &limits, sizeof(limits))) {
        std::wcerr << L"Cannot create isolation process supervisor: " << GetLastError() << L"\n"; return 1;
    }
    STARTUPINFOW startup{}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION info{};
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED,
                        nullptr, workingDirectory.c_str(), &startup, &info)) {
        std::wcerr << L"Cannot start Steam isolation process: " << GetLastError() << L"\n"; return 1;
    }
    Handle process(info.hProcess), thread(info.hThread);
    auto stop = [&]() { TerminateProcess(process.value, 1); WaitForSingleObject(process.value, 5000); return DWORD{1}; };
    if (!AssignProcessToJobObject(job.value, process.value)) {
        std::wcerr << L"Cannot supervise isolation process: " << GetLastError() << L"\n"; return stop();
    }
    std::wstring error;
    if (!hook.empty() && !Inject(process.value, hook, error)) {
        std::wcerr << L"Steam isolation failed: " << error << L"\n"; return stop();
    }
    if (ResumeThread(thread.value) == static_cast<DWORD>(-1)) {
        std::wcerr << L"Cannot resume isolated process: " << GetLastError() << L"\n"; return stop();
    }
    if (readyEvent && !SetEvent(readyEvent)) {
        std::wcerr << L"Cannot signal Steam isolation readiness: " << GetLastError() << L"\n"; return stop();
    }
    if (WaitForSingleObject(process.value, INFINITE) != WAIT_OBJECT_0) return stop();
    DWORD exitCode = 1; GetExitCodeProcess(process.value, &exitCode);
    // Steam's updater can exit after creating its replacement. Keep owning the
    // job until the entire descendant tree exits, rather than killing that replacement.
    for (;;) {
        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting{};
        if (!QueryInformationJobObject(job.value, JobObjectBasicAccountingInformation,
                                     &accounting, sizeof(accounting), nullptr)) {
            std::wcerr << L"Cannot monitor isolation process tree: " << GetLastError() << L"\n"; return 1;
        }
        if (!accounting.ActiveProcesses) return exitCode;
        Sleep(250);
    }
}
int wmain() {
    std::wstring raw(GetCommandLineW()), token, steam, readyEventName;
    size_t cursor = 0;
    bool valid = NextToken(raw, cursor, token);
    [[maybe_unused]] size_t prefixStart = cursor;
    valid = valid && NextToken(raw, cursor, token) && token == L"--steam";
    valid = valid && NextToken(raw, cursor, steam) && !steam.empty();
    valid = valid && NextToken(raw, cursor, token);
    if (valid && token == L"--ready-event") {
        valid = NextToken(raw, cursor, readyEventName) && !readyEventName.empty() && NextToken(raw, cursor, token);
    }
    valid = valid && token == L"--";
    const bool absolutePath = (steam.size() > 2 && steam[1] == L':' && (steam[2] == L'\\' || steam[2] == L'/')) ||
        (steam.size() > 2 && steam[0] == L'\\' && steam[1] == L'\\');
    const auto basename = steam.substr(steam.find_last_of(L"\\/") + 1);
    if (!valid || !absolutePath || steam.find(L'"') != std::wstring::npos || _wcsicmp(basename.c_str(), L"steam.exe") != 0) {
        std::wcerr << L"Usage: MultiSeat.SteamLauncher.exe --steam <absolute steam.exe> [--ready-event <name>] -- <Steam arguments>\n"; return 2;
    }
    Handle readyEvent(readyEventName.empty() ? nullptr : OpenEventW(EVENT_MODIFY_STATE, FALSE, readyEventName.c_str()));
    if (!readyEventName.empty() && !readyEvent.value) {
        std::wcerr << L"Cannot open Steam isolation readiness event: " << GetLastError() << L"\n"; return 2;
    }
    std::wstring directory = ExecutableDirectory();
    WORD machine = TargetMachine(steam);
    if (machine != IMAGE_FILE_MACHINE_I386 && machine != IMAGE_FILE_MACHINE_AMD64) {
        std::wcerr << L"Steam executable is missing, invalid, or uses an unsupported architecture\n"; return 2;
    }
    std::vector<wchar_t> currentDirectory(32768);
    DWORD cwdLength = GetCurrentDirectoryW(static_cast<DWORD>(currentDirectory.size()), currentDirectory.data());
    if (!cwdLength || cwdLength >= currentDirectory.size()) {
        std::wcerr << L"Cannot resolve launch working directory\n"; return 2;
    }
    std::wstring workingDirectory(currentDirectory.data(), cwdLength);
#ifdef _WIN64
    if (machine == IMAGE_FILE_MACHINE_I386) {
        std::wstring helper = directory + L"\\MultiSeat.SteamLauncher32.exe";
        return static_cast<int>(Run(helper, L"\"" + helper + L"\"" + raw.substr(prefixStart), L"", workingDirectory));
    }
    std::wstring hook = directory + L"\\MultiSeat.SteamHook.dll";
#else
    if (machine != IMAGE_FILE_MACHINE_I386) {
        std::wcerr << L"Use the 64-bit isolation launcher for 64-bit Steam\n"; return 2;
    }
    std::wstring hook = directory + L"\\MultiSeat.SteamHook32.dll";
#endif
    return static_cast<int>(Run(steam, L"\"" + steam + L"\"" + raw.substr(cursor), hook, workingDirectory, readyEvent.value));
}
