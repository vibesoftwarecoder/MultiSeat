// Harmless stand-in for Steam: records arguments/environment and optionally holds a test mutex.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <vector>
#include <fstream>
#include <filesystem>
#include <iostream>

int wmain(int argc, wchar_t** argv) {
    std::wstring report, mutexName, childReport, restartReport;
    DWORD holdMs = 0;
    DWORD childHoldMs = 0;
    bool detachChild = false;
    for (int i = 1; i < argc; ++i) {
        std::wstring arg(argv[i]);
        if (arg == L"--report" && i + 1 < argc) report = argv[++i];
        else if (arg == L"--child-report" && i + 1 < argc) childReport = argv[++i];
        else if (arg == L"--restart-report" && i + 1 < argc) restartReport = argv[++i];
        else if (arg == L"--mutex" && i + 1 < argc) mutexName = argv[++i];
        else if (arg == L"--hold-ms" && i + 1 < argc) holdMs = wcstoul(argv[++i], nullptr, 10);
        else if (arg == L"--child-hold-ms" && i + 1 < argc) childHoldMs = wcstoul(argv[++i], nullptr, 10);
        else if (arg == L"--detach-child") detachChild = true;
    }
    HANDLE mutex = nullptr;
    DWORD mutexError = 0;
    if (!mutexName.empty()) {
        SetLastError(ERROR_SUCCESS);
        mutex = CreateMutexW(nullptr, FALSE, mutexName.c_str());
        mutexError = GetLastError();
        if (!mutex) return 3;
    }
    if (!report.empty()) {
        std::wofstream output{std::filesystem::path(report)};
        if (!output) { if (mutex) CloseHandle(mutex); return 4; }
        output << L"command=" << GetCommandLineW() << L"\n";
        const char* narrow = GetCommandLineA();
        output << L"commandA=";
        for (; *narrow; ++narrow) output << static_cast<wchar_t>(static_cast<unsigned char>(*narrow));
        output << L"\n";
        for (int i = 0; i < argc; ++i) output << L"arg" << i << L"=" << argv[i] << L"\n";
        DWORD session = 0; ProcessIdToSessionId(GetCurrentProcessId(), &session);
        output << L"session=" << session << L"\nmutexError=" << mutexError << L"\n";
        const wchar_t* variables[] = {L"steam_master_ipc_name_override", L"LOCALAPPDATA", L"MULTISEAT_STEAM_ISOLATION"};
        for (auto variable : variables) {
            DWORD needed = GetEnvironmentVariableW(variable, nullptr, 0);
            std::vector<wchar_t> value(needed ? needed : 1);
            if (needed) GetEnvironmentVariableW(variable, value.data(), needed);
            output << variable << L"=" << value.data() << L"\n";
        }
        // A dedicated fixture-owned key avoids changing real Steam settings.
        std::wstring keyPath = L"Software\\MultiSeat\\SteamIsolationTests\\" + std::to_wstring(GetCurrentProcessId());
        HKEY key = nullptr;
        LONG registryStatus = RegCreateKeyExW(HKEY_CURRENT_USER, keyPath.c_str(), 0, nullptr, 0,
            KEY_SET_VALUE | KEY_QUERY_VALUE, nullptr, &key, nullptr);
        DWORD steamPid = 123456, readPid = 0, size = sizeof(readPid), type = 0;
        DWORD nullTypePid = 0;
        wchar_t stringPid[64]{};
        if (registryStatus == ERROR_SUCCESS) {
            registryStatus = RegSetValueExW(key, L"SteamPID", 0, REG_DWORD,
                reinterpret_cast<const BYTE*>(&steamPid), sizeof(steamPid));
            if (registryStatus == ERROR_SUCCESS) registryStatus = RegQueryValueExW(key, L"SteamPID", nullptr,
                &type, reinterpret_cast<BYTE*>(&readPid), &size);
            size = sizeof(nullTypePid);
            if (registryStatus == ERROR_SUCCESS) registryStatus = RegQueryValueExW(key, L"SteamPID", nullptr,
                nullptr, reinterpret_cast<BYTE*>(&nullTypePid), &size);
            const wchar_t stringValue[] = L"fixture-nonDWORD";
            if (registryStatus == ERROR_SUCCESS) registryStatus = RegSetValueExW(key, L"SteamPID", 0, REG_SZ,
                reinterpret_cast<const BYTE*>(stringValue), sizeof(stringValue));
            size = sizeof(stringPid);
            if (registryStatus == ERROR_SUCCESS) registryStatus = RegQueryValueExW(key, L"SteamPID", nullptr,
                nullptr, reinterpret_cast<BYTE*>(stringPid), &size);
            RegCloseKey(key);
            RegDeleteKeyW(HKEY_CURRENT_USER, keyPath.c_str());
        }
        output << L"registryStatus=" << registryStatus << L"\nsteamPid=" << readPid
            << L"\nsteamPidNullType=" << nullTypePid << L"\nsteamPidString=" << stringPid << L"\n";
    }
    if (!childReport.empty() || !restartReport.empty()) {
        std::vector<wchar_t> path(32768);
        DWORD n = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
        if (!n || n >= path.size()) { if (mutex) CloseHandle(mutex); return 6; }
        std::wstring helper(path.data(), n);
        helper = helper.substr(0, helper.find_last_of(L"\\/")) +
            (restartReport.empty() ? L"\\steamwebhelper.exe" : L"\\steam.exe");
        std::wstring command;
        if (!restartReport.empty()) {
            if (childReport.empty()) childReport = restartReport + L".browser";
            command = L"\"" + helper + L"\" --report \"" + restartReport + L"\" --child-report \"" + childReport +
                L"\" --child-hold-ms " + std::to_wstring(childHoldMs) + L" --detach-child";
        } else {
            command = L"\"" + helper + L"\" --report \"" + childReport +
                L"\" -cachedir=C:\\fixture\\htmlcache --log-file=C:\\fixture\\cef_log.txt --hold-ms " + std::to_wstring(childHoldMs);
        }
        // Explicit environment exercises propagation even when the caller doesn't inherit it.
        wchar_t environment[] = L"FIXTURE_EXPLICIT=1\0\0";
        STARTUPINFOW startup{}; startup.cb = sizeof(startup);
        PROCESS_INFORMATION child{};
        if (!CreateProcessW(helper.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_UNICODE_ENVIRONMENT,
                            environment, nullptr, &startup, &child)) {
            std::wcerr << L"Fixture child creation failed: " << GetLastError() << L"\n";
            if (mutex) CloseHandle(mutex); return 7;
        }
        CloseHandle(child.hThread);
        if (detachChild || !restartReport.empty()) { CloseHandle(child.hProcess); if (mutex) CloseHandle(mutex); return 0; }
        DWORD wait = WaitForSingleObject(child.hProcess, 10000), exitCode = 0;
        if (wait != WAIT_OBJECT_0) TerminateProcess(child.hProcess, 8);
        else GetExitCodeProcess(child.hProcess, &exitCode);
        CloseHandle(child.hProcess);
        if (wait != WAIT_OBJECT_0 || exitCode) { if (mutex) CloseHandle(mutex); return 8; }
    }
    if (holdMs) Sleep(holdMs);
    if (mutex) CloseHandle(mutex);
    return mutexError == ERROR_ALREADY_EXISTS ? 5 : 0;
}
