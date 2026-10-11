#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winternl.h>
#include <sddl.h>
#include <MinHook.h>
#include <cstddef>
#include <cstdint>
#include <cwctype>
#include "IsolationPolicy.h"
#include "Inject.h"

namespace {
using namespace steam_isolation;
std::wstring ipcName, commandW;
std::string commandA;
bool isSteam = false;
HMODULE ownModule = nullptr;
std::wstring ownDllPath;
LONG initialization = 0;
thread_local bool creating = false;

// Stable Windows process-parameter prefix, following winsiderss/phnt ntrtl.h.
// Only normalized parameters with the full prefix are admitted for rewriting.
struct CurrentDir { UNICODE_STRING path; HANDLE handle; };
struct DriveDir { USHORT flags, length; ULONG timestamp; ANSI_STRING path; };
struct Parameters {
    ULONG maximumLength, length, flags, debugFlags;
    HANDLE console; ULONG consoleFlags;
    HANDLE input, output, error;
    CurrentDir currentDirectory;
    UNICODE_STRING dllPath, image, command;
    PVOID environment;
    ULONG startingX, startingY, countX, countY, countCharsX, countCharsY, fillAttribute;
    ULONG windowFlags, showWindowFlags;
    UNICODE_STRING windowTitle, desktop, shell, runtime;
    DriveDir drives[32];
    ULONG_PTR environmentSize, environmentVersion;
};
#ifdef _WIN64
static_assert(offsetof(Parameters, command) == 0x70 && offsetof(Parameters, environmentSize) == 0x3f0);
#else
static_assert(offsetof(Parameters, command) == 0x40 && offsetof(Parameters, environmentSize) == 0x290);
#endif
using CreateUser = NTSTATUS (NTAPI*)(PHANDLE, PHANDLE, ACCESS_MASK, ACCESS_MASK,
    POBJECT_ATTRIBUTES, POBJECT_ATTRIBUTES, ULONG, ULONG, PVOID, PVOID, PVOID);
CreateUser originalCreate = nullptr;
decltype(&CreateProcessW) createW = nullptr;
decltype(&CreateProcessA) createA = nullptr;
decltype(&RegQueryValueExW) queryW = nullptr;
decltype(&RegQueryValueExA) queryA = nullptr;
decltype(&RegSetValueExW) setW = nullptr;
decltype(&RegSetValueExA) setA = nullptr;
decltype(&RegDeleteValueW) deleteW = nullptr;
decltype(&RegDeleteValueA) deleteA = nullptr;

bool Temporary(const wchar_t* value) {
    return isSteam && value && (!_wcsicmp(value, L"TempAppPath") || !_wcsicmp(value, L"TempAppCmdLine"));
}
bool Temporary(const char* value) {
    return isSteam && value && (!_stricmp(value, "TempAppPath") || !_stricmp(value, "TempAppCmdLine"));
}
struct UserSteamKey {
    HKEY value = nullptr;
    LSTATUS status = ERROR_INVALID_HANDLE;
    explicit UserSteamKey(REGSAM access, bool create = false) {
        if (!create) status = RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\Valve\\Steam", 0, access, &value);
        else status = RegCreateKeyExW(HKEY_CURRENT_USER, L"Software\\Valve\\Steam", 0, nullptr, 0, access, nullptr, &value, nullptr);
    }
    ~UserSteamKey() { if (value) RegCloseKey(value); }
};
LSTATUS WINAPI QueryW(HKEY key, LPCWSTR value, LPDWORD reserved, LPDWORD type, LPBYTE data, LPDWORD size) {
    LSTATUS result;
    DWORD actualType = 0; auto returnedType = type ? type : &actualType;
    if (Temporary(value)) { UserSteamKey user(KEY_QUERY_VALUE); if (!user.value) return user.status; result = queryW(user.value, value, reserved, returnedType, data, size); }
    else result = queryW(key, value, reserved, returnedType, data, size);
    if (result == ERROR_SUCCESS && value && !_wcsicmp(value, L"SteamPID") && data && size && *size == sizeof(DWORD) && *returnedType == REG_DWORD) {
        DWORD zero = 0; memcpy(data, &zero, sizeof(zero));
    }
    return result;
}
LSTATUS WINAPI QueryA(HKEY key, LPCSTR value, LPDWORD reserved, LPDWORD type, LPBYTE data, LPDWORD size) {
    LSTATUS result;
    DWORD actualType = 0; auto returnedType = type ? type : &actualType;
    if (Temporary(value)) { UserSteamKey user(KEY_QUERY_VALUE); if (!user.value) return user.status; result = queryA(user.value, value, reserved, returnedType, data, size); }
    else result = queryA(key, value, reserved, returnedType, data, size);
    if (result == ERROR_SUCCESS && value && !_stricmp(value, "SteamPID") && data && size && *size == sizeof(DWORD) && *returnedType == REG_DWORD) {
        DWORD zero = 0; memcpy(data, &zero, sizeof(zero));
    }
    return result;
}
LSTATUS WINAPI SetW(HKEY key, LPCWSTR value, DWORD reserved, DWORD type, const BYTE* data, DWORD size) {
    if (!Temporary(value)) return setW(key, value, reserved, type, data, size);
    UserSteamKey user(KEY_SET_VALUE, true); return user.value ? setW(user.value, value, reserved, type, data, size) : user.status;
}
LSTATUS WINAPI SetA(HKEY key, LPCSTR value, DWORD reserved, DWORD type, const BYTE* data, DWORD size) {
    if (!Temporary(value)) return setA(key, value, reserved, type, data, size);
    UserSteamKey user(KEY_SET_VALUE, true); return user.value ? setA(user.value, value, reserved, type, data, size) : user.status;
}
LSTATUS WINAPI DeleteW(HKEY key, LPCWSTR value) {
    if (!Temporary(value)) return deleteW(key, value);
    UserSteamKey user(KEY_SET_VALUE); return user.value ? deleteW(user.value, value) : user.status;
}
LSTATUS WINAPI DeleteA(HKEY key, LPCSTR value) {
    if (!Temporary(value)) return deleteA(key, value);
    UserSteamKey user(KEY_SET_VALUE); return user.value ? deleteA(user.value, value) : user.status;
}
LPWSTR WINAPI CommandW() { return commandW.data(); }
LPSTR WINAPI CommandA() { return commandA.data(); }
bool SteamLaunch(LPCWSTR application, LPCWSTR command) {
    if (application) return SteamImage(application);
    if (!command) return false;
    int count = 0; LPWSTR* args = CommandLineToArgvW(command, &count);
    bool result = args && count && SteamImage(args[0]); if (args) LocalFree(args); return result;
}
std::wstring Wide(LPCSTR value) {
    if (!value) return {};
    int size = MultiByteToWideChar(CP_ACP, 0, value, -1, nullptr, 0);
    if (!size) return {};
    std::wstring result(size, 0); MultiByteToWideChar(CP_ACP, 0, value, -1, result.data(), size);
    result.resize(size - 1); return result;
}
BOOL FinishRestart(PROCESS_INFORMATION* pi, DWORD flags) {
    std::wstring error;
    try {
        if (Inject(pi->hProcess, ownDllPath, error) &&
            ((flags & CREATE_SUSPENDED) || ResumeThread(pi->hThread) != static_cast<DWORD>(-1))) return TRUE;
    } catch (...) { error = L"Resource allocation failed"; }
    OutputDebugStringW((L"MultiSeat Steam restart isolation failed: " + error).c_str());
    TerminateProcess(pi->hProcess, 1); CloseHandle(pi->hThread); CloseHandle(pi->hProcess); *pi = {};
    SetLastError(ERROR_DLL_INIT_FAILED); return FALSE;
}
BOOL WINAPI CreateW(LPCWSTR app, LPWSTR command, LPSECURITY_ATTRIBUTES processAttributes,
    LPSECURITY_ATTRIBUTES threadAttributes, BOOL inherit, DWORD flags, LPVOID environment,
    LPCWSTR directory, LPSTARTUPINFOW startup, LPPROCESS_INFORMATION pi) {
    bool restart = SteamLaunch(app, command);
    BOOL result = createW(app, command, processAttributes, threadAttributes, inherit,
        restart ? flags | CREATE_SUSPENDED : flags, environment, directory, startup, pi);
    return result && restart ? FinishRestart(pi, flags) : result;
}
BOOL WINAPI CreateA(LPCSTR app, LPSTR command, LPSECURITY_ATTRIBUTES processAttributes,
    LPSECURITY_ATTRIBUTES threadAttributes, BOOL inherit, DWORD flags, LPVOID environment,
    LPCSTR directory, LPSTARTUPINFOA startup, LPPROCESS_INFORMATION pi) {
    auto wideApp = Wide(app), wideCommand = Wide(command);
    bool restart = SteamLaunch(app ? wideApp.c_str() : nullptr, command ? wideCommand.c_str() : nullptr);
    BOOL result = createA(app, command, processAttributes, threadAttributes, inherit,
        restart ? flags | CREATE_SUSPENDED : flags, environment, directory, startup, pi);
    return result && restart ? FinishRestart(pi, flags) : result;
}
NTSTATUS NTAPI CreateUserHook(PHANDLE process, PHANDLE thread, ACCESS_MASK pa, ACCESS_MASK ta,
    POBJECT_ATTRIBUTES po, POBJECT_ATTRIBUTES to, ULONG pf, ULONG tf, PVOID parameters, PVOID info, PVOID attributes) {
    auto invoke = [&]() { return originalCreate(process, thread, pa, ta, po, to, pf, tf, parameters, info, attributes); };
    if (creating || !parameters) return invoke();
    auto* p = static_cast<Parameters*>(parameters);
    if (!(p->flags & 1) || p->length < offsetof(Parameters, environmentVersion) + sizeof(ULONG_PTR)) return invoke();
    creating = true;
    struct Reset { ~Reset() { creating = false; } } reset;
    UNICODE_STRING saved = p->command;
    void* oldEnvironment = p->environment;
    ULONG_PTR oldSize = p->environmentSize;
    struct Restore {
        Parameters* p; UNICODE_STRING command; PVOID environment; ULONG_PTR size;
        ~Restore() { p->command = command; p->environment = environment; p->environmentSize = size; }
    } restore{p, saved, oldEnvironment, oldSize};
    try {
        std::wstring changed;
        if (saved.Buffer && !(saved.Length % sizeof(wchar_t))) {
            changed = RewriteCommand(std::wstring(saved.Buffer, saved.Length / sizeof(wchar_t)), ipcName);
            if (changed.size() > 32766) return static_cast<NTSTATUS>(0xc000000dL);
            p->command.Buffer = changed.data();
            p->command.Length = static_cast<USHORT>(changed.size() * sizeof(wchar_t));
            p->command.MaximumLength = static_cast<USHORT>((changed.size() + 1) * sizeof(wchar_t));
        }
        std::vector<wchar_t> environment;
        if (oldEnvironment && oldSize >= 4 && oldSize <= 16 * 1024 * 1024 && !(oldSize % sizeof(wchar_t))) {
            environment = RewriteEnvironment(static_cast<wchar_t*>(oldEnvironment), oldSize / sizeof(wchar_t), ipcName);
            if (environment.empty()) return static_cast<NTSTATUS>(0xc000000dL);
            p->environment = environment.data(); p->environmentSize = environment.size() * sizeof(wchar_t);
        }
        return invoke();
    } catch (...) { return static_cast<NTSTATUS>(0xc0000017L); }
}
template<class T> bool Hook(const wchar_t* module, const char* function, void* replacement, T* original) {
    HMODULE handle = GetModuleHandleW(module);
    auto address = handle ? GetProcAddress(handle, function) : nullptr;
    return address && MH_CreateHook(reinterpret_cast<void*>(address), replacement, reinterpret_cast<void**>(original)) == MH_OK;
}
}

#if defined(_MSC_VER) && !defined(_WIN64)
#pragma comment(linker, "/EXPORT:MultiSeatInitialize=_MultiSeatInitialize@4")
#endif
extern "C" __declspec(dllexport) DWORD WINAPI MultiSeatInitialize(void*) {
    if (InterlockedCompareExchange(&initialization, 1, 0) != 0) return initialization == 2 ? 1 : 0;
    try {
        DWORD session = 0;
        if (!ProcessIdToSessionId(GetCurrentProcessId(), &session)) return 0;
        HANDLE token = nullptr; if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return 0;
        DWORD bytes = 0; GetTokenInformation(token, TokenUser, nullptr, 0, &bytes);
        std::vector<BYTE> data(bytes);
        bool got = GetTokenInformation(token, TokenUser, data.data(), bytes, &bytes) != FALSE;
        CloseHandle(token); if (!got) return 0;
        LPWSTR sid = nullptr; if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(data.data())->User.Sid, &sid)) return 0;
        uint64_t hash = 14695981039346656037ULL;
        for (const wchar_t* c = sid; *c; ++c) { hash ^= static_cast<uint64_t>(*c); hash *= 1099511628211ULL; }
        LocalFree(sid);
        ipcName = L"MultiSeat_" + std::to_wstring(session) + L"_" + std::to_wstring(hash);
        wchar_t image[32768]; DWORD length = GetModuleFileNameW(nullptr, image, 32768);
        if (!length || length >= 32768 || !SteamImage(std::wstring(image, length))) return 0;
        isSteam = true;
        wchar_t dllPath[32768]; DWORD dllLength = GetModuleFileNameW(ownModule, dllPath, 32768);
        if (!dllLength || dllLength >= 32768) return 0;
        ownDllPath.assign(dllPath, dllLength);
        commandW = RewriteCommand(GetCommandLineW(), ipcName);
        int narrowSize = WideCharToMultiByte(CP_ACP, 0, commandW.c_str(), -1, nullptr, 0, nullptr, nullptr);
        if (!narrowSize) return 0;
        commandA.resize(narrowSize); WideCharToMultiByte(CP_ACP, 0, commandW.c_str(), -1, commandA.data(), narrowSize, nullptr, nullptr);
        if (!SetEnvironmentVariableW(L"steam_master_ipc_name_override", ipcName.c_str())) return 0;
        if (MH_Initialize() != MH_OK) return 0;
        LPWSTR (WINAPI* originalCommandW)() = nullptr; LPSTR (WINAPI* originalCommandA)() = nullptr;
        bool ok = Hook(L"ntdll.dll", "NtCreateUserProcess", reinterpret_cast<void*>(CreateUserHook), &originalCreate)
            // Win32 completes subsystem registration before restart injection; doing this
            // inside NtCreateUserProcess is too early for remote loader initialization.
            && Hook(L"kernelbase.dll", "CreateProcessW", reinterpret_cast<void*>(CreateW), &createW)
            && Hook(L"kernelbase.dll", "CreateProcessA", reinterpret_cast<void*>(CreateA), &createA)
            && Hook(L"kernelbase.dll", "GetCommandLineW", reinterpret_cast<void*>(CommandW), &originalCommandW)
            && Hook(L"kernelbase.dll", "GetCommandLineA", reinterpret_cast<void*>(CommandA), &originalCommandA)
            && Hook(L"kernelbase.dll", "RegQueryValueExW", reinterpret_cast<void*>(QueryW), &queryW)
            && Hook(L"kernelbase.dll", "RegQueryValueExA", reinterpret_cast<void*>(QueryA), &queryA)
            && Hook(L"kernelbase.dll", "RegSetValueExW", reinterpret_cast<void*>(SetW), &setW)
            && Hook(L"kernelbase.dll", "RegSetValueExA", reinterpret_cast<void*>(SetA), &setA)
            && Hook(L"kernelbase.dll", "RegDeleteValueW", reinterpret_cast<void*>(DeleteW), &deleteW)
            && Hook(L"kernelbase.dll", "RegDeleteValueA", reinterpret_cast<void*>(DeleteA), &deleteA);
        if (!ok || MH_EnableHook(MH_ALL_HOOKS) != MH_OK) { MH_Uninitialize(); return 0; }
        InterlockedExchange(&initialization, 2); return 1;
    } catch (...) { return 0; }
}
BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) { ownModule = module; DisableThreadLibraryCalls(module); }
    return TRUE;
}
