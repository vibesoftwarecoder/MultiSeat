#include "CommandTokens.h"
#include "Inject.h"
#include "IsolationPolicy.h"
#include "Log.h"

#include <tlhelp32.h>

using namespace steam_isolation;

// The program a Steam shortcut, the steam:// handler and the Run entry point at, in place of steam.exe:
//
//     MultiSeat.SteamShim.exe --steam <absolute steam.exe> -- <the arguments steam.exe was given>
//
// It never starts a plain Steam. In an account that already has an isolated Steam it hands the
// arguments to that one (launcher --forward); otherwise it starts one (launcher). The design, and the
// measurements behind it, are in docs/design/steam-shim.md.
//
// Like the launcher it is a Windows-subsystem program with no console, so everything it has to say
// goes to launcher.log, and a failure the user must notice is also shown in a message box.

static const wchar_t kLauncher[] = L"MultiSeat.SteamLauncher.exe";
static const wchar_t kMutex[] = L"Local\\MultiSeatSteamShim";  // Local\: one per session, so one per account

static bool Quiet(const std::wstring& tail) {
    // A Run entry passes -silent. A modal box at logon would be worse than the failure, so that case is
    // logged and nothing else.
    return tail.find(L"-silent") != std::wstring::npos;
}
static int Fail(const std::wstring& message, const std::wstring& tail, int code) {
    AppendLog(L"Steam shim refused: " + message);
    if (!Quiet(tail)) {
        MessageBoxW(nullptr, (message + L"\n\nSteam was not started. Details are in\n%LOCALAPPDATA%\\MultiSeat\\SteamIsolation\\launcher.log").c_str(),
                    L"MultiSeat Steam isolation", MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
    }
    return code;
}

// Where the isolated Steam writes its pid: the user's own Valve\Steam key. The smoke tests point this
// at a key of their own, so a real Steam running on the machine that runs them is never mistaken for
// the stand-in and handed the stand-in's arguments.
static std::wstring StateKey() {
    wchar_t buffer[260];
    const DWORD length = GetEnvironmentVariableW(L"MULTISEAT_STEAM_SHIM_KEY", buffer, 260);
    return length && length < 260 ? std::wstring(buffer, length) : std::wstring(L"Software\\Valve\\Steam");
}

// Is this pid a live steam.exe of this session with the hook DLL loaded? The hook is what makes the
// process an isolated one, so it is what we look for; a plain Steam that happens to be running does not count.
static bool IsIsolatedSteam(DWORD pid) {
    if (!pid) return false;
    Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
    if (!process.value) return false;
    DWORD exitCode = 0;
    if (!GetExitCodeProcess(process.value, &exitCode) || exitCode != STILL_ACTIVE) return false;
    std::vector<wchar_t> image(32768);
    DWORD length = static_cast<DWORD>(image.size());
    if (!QueryFullProcessImageNameW(process.value, 0, image.data(), &length)) return false;
    if (!SteamImage(std::wstring(image.data(), length))) return false;
    DWORD mine = 0, theirs = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &mine) || !ProcessIdToSessionId(pid, &theirs) || mine != theirs) return false;
    return FindModule(pid, L"MultiSeat.SteamHook.dll", false) || FindModule(pid, L"MultiSeat.SteamHook32.dll", false);
}

// The first isolated steam.exe in this session, or 0.
static DWORD FindIsolatedSteamInSession() {
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    if (snapshot.value == INVALID_HANDLE_VALUE) return 0;
    PROCESSENTRY32W entry{}; entry.dwSize = sizeof(entry);
    for (BOOL more = Process32FirstW(snapshot.value, &entry); more; more = Process32NextW(snapshot.value, &entry))
        if (!_wcsicmp(entry.szExeFile, L"steam.exe") && IsIsolatedSteam(entry.th32ProcessID)) return entry.th32ProcessID;
    return 0;
}

// The isolated Steam of this account, or 0. SteamPID in the user's own key (the hook keeps it there)
// names it when that is still true. A plain steam.exe also writes SteamPID in that key, so a Steam
// started some other way (a taskbar pin, a script) can leave it pointing at a process with no hook.
// The hook-carrying Steam of this session is the answer then; starting a second one would replace
// the first one's session. The smoke tests point the key elsewhere, so they never search the machine.
static DWORD IsolatedMaster() {
    DWORD pid = 0, size = sizeof(pid);
    if (RegGetValueW(HKEY_CURRENT_USER, StateKey().c_str(), L"SteamPID", RRF_RT_REG_DWORD,
                     nullptr, &pid, &size) == ERROR_SUCCESS && IsIsolatedSteam(pid)) return pid;
    if (GetEnvironmentVariableW(L"MULTISEAT_STEAM_SHIM_KEY", nullptr, 0)) return 0;
    const DWORD found = FindIsolatedSteamInSession();
    if (found) {
        // The forwarding steam.exe hands its command to whatever SteamPID names, so the value has to be
        // right before the launcher runs, not only in what the shim believes.
        const LSTATUS repaired = RegSetKeyValueW(HKEY_CURRENT_USER, StateKey().c_str(), L"SteamPID", REG_DWORD, &found, sizeof(found));
        AppendLog(L"Steam shim: SteamPID did not name an isolated Steam; found " + std::to_wstring(found) + L" by scanning" +
                  (repaired == ERROR_SUCCESS ? L" and set SteamPID to it" : L", and could not set SteamPID (" + std::to_wstring(repaired) + L")"));
    }
    return found;
}

static bool StartLauncher(const std::wstring& launcher, const std::wstring& steam, const std::wstring& tail,
                          bool forward, Handle& process) {
    // `tail` is the raw text after "--", leading space included, exactly as the caller wrote it.
    std::wstring command = L"\"" + launcher + L"\" --steam \"" + steam + L"\"" + (forward ? L" --forward" : L"") + L" --" + tail;
    STARTUPINFOW startup{}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION info{};
    if (!CreateProcessW(launcher.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &info)) {
        AppendLog(L"Steam shim: cannot start the launcher: " + std::to_wstring(GetLastError()));
        return false;
    }
    CloseHandle(info.hThread);
    process.value = info.hProcess;
    return true;
}

int wmain() {
    std::wstring raw(GetCommandLineW()), token, steam;
    size_t cursor = 0;
    bool valid = NextToken(raw, cursor, token);                                  // our own name
    valid = valid && NextToken(raw, cursor, token) && token == L"--steam";
    valid = valid && NextToken(raw, cursor, steam) && !steam.empty();
    valid = valid && NextToken(raw, cursor, token) && token == L"--";
    const std::wstring tail = valid ? raw.substr(cursor) : std::wstring();
    const bool absolute = (steam.size() > 2 && steam[1] == L':' && (steam[2] == L'\\' || steam[2] == L'/')) ||
        (steam.size() > 2 && steam[0] == L'\\' && steam[1] == L'\\');
    if (!valid || !absolute || steam.find(L'"') != std::wstring::npos || !SteamImage(steam)) {
        return Fail(L"A Steam shortcut is set up wrongly. Expected: MultiSeat.SteamShim.exe --steam <absolute steam.exe> -- <arguments>", raw, 2);
    }
    const std::wstring directory = ExecutableDirectory();
    const std::wstring launcher = directory + L"\\" + kLauncher;
    const WORD machine = TargetMachine(steam);
    if (machine != IMAGE_FILE_MACHINE_I386 && machine != IMAGE_FILE_MACHINE_AMD64) {
        return Fail(L"Steam is missing or is not a program Windows can run: " + steam, tail, 2);
    }
    const std::wstring hook = directory + (machine == IMAGE_FILE_MACHINE_I386 ? L"\\MultiSeat.SteamHook32.dll" : L"\\MultiSeat.SteamHook.dll");
    if (!RegularFileExists(launcher) || !RegularFileExists(hook)) {
        return Fail(L"The Steam isolation files are missing from " + directory, tail, 2);
    }

    // One launch at a time per account. Two quick clicks would otherwise both find no Steam, both start
    // one, and the second would log in again and replace the first one's session.
    Handle mutex(CreateMutexW(nullptr, FALSE, kMutex));
    const bool locked = mutex.value && WaitForSingleObject(mutex.value, 40000) != WAIT_TIMEOUT;
    if (!locked) AppendLog(L"Steam shim: another launch is still in progress after 40 s; going on without the lock");

    int result = 0;
    const DWORD master = IsolatedMaster();
    Handle process;
    if (master) {
        AppendLog(L"Steam shim: isolated Steam " + std::to_wstring(master) + L" is running; handing it the command");
        if (!StartLauncher(launcher, steam, tail, true, process)) result = Fail(L"The Steam isolation launcher could not be started.", tail, 1);
        else if (WaitForSingleObject(process.value, 20000) == WAIT_OBJECT_0) {
            DWORD code = 1; GetExitCodeProcess(process.value, &code);
            AppendLog(L"Steam shim: command handed over, launcher exit " + std::to_wstring(code));
        } else AppendLog(L"Steam shim: the hand-over was still running after 20 s");
    } else {
        AppendLog(L"Steam shim: no isolated Steam in this account; starting one");
        if (!StartLauncher(launcher, steam, tail, false, process)) result = Fail(L"The Steam isolation launcher could not be started.", tail, 1);
        else {
            // Keep the lock until the new Steam has registered itself, so a second click forwards to it.
            const ULONGLONG deadline = GetTickCount64() + 25000;
            while (GetTickCount64() < deadline && !IsolatedMaster()) {
                if (WaitForSingleObject(process.value, 200) == WAIT_OBJECT_0) {
                    DWORD code = 1; GetExitCodeProcess(process.value, &code);
                    if (code != 0) { result = Fail(L"Steam isolation failed to start Steam (launcher exit " + std::to_wstring(code) + L"). See launcher.log.", tail, 1); }
                    break;
                }
            }
        }
    }
    if (locked) ReleaseMutex(mutex.value);
    return result;
}
