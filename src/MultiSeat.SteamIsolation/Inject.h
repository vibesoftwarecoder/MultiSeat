#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <psapi.h>
#include <string>
#include <vector>
#include <cstdint>

namespace steam_isolation {
struct Handle {
    HANDLE value = nullptr;
    explicit Handle(HANDLE h = nullptr) : value(h) {}
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};
inline std::wstring ExecutableDirectory() {
    std::vector<wchar_t> path(32768);
    DWORD n = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (!n || n >= path.size()) return {};
    std::wstring result(path.data(), n);
    return result.substr(0, result.find_last_of(L"\\/"));
}
inline WORD TargetMachine(const std::wstring& path) {
    Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE,
                            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (file.value == INVALID_HANDLE_VALUE) return 0;
    IMAGE_DOS_HEADER dos{}; DWORD read = 0;
    if (!ReadFile(file.value, &dos, sizeof(dos), &read, nullptr) || read != sizeof(dos) ||
        dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew < sizeof(dos)) return 0;
    LARGE_INTEGER offset{}; offset.QuadPart = dos.e_lfanew;
    if (!SetFilePointerEx(file.value, offset, nullptr, FILE_BEGIN)) return 0;
    DWORD signature = 0; IMAGE_FILE_HEADER header{};
    if (!ReadFile(file.value, &signature, sizeof(signature), &read, nullptr) ||
        read != sizeof(signature) || signature != IMAGE_NT_SIGNATURE ||
        !ReadFile(file.value, &header, sizeof(header), &read, nullptr) || read != sizeof(header)) return 0;
    return header.Machine;
}
inline uintptr_t FindModule(DWORD pid, const std::wstring& name, bool fullPath) {
    HANDLE snapshot = INVALID_HANDLE_VALUE;
    for (int attempt = 0; attempt < 8; ++attempt) {
        snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
        if (snapshot != INVALID_HANDLE_VALUE || GetLastError() != ERROR_BAD_LENGTH) break;
    }
    Handle owner(snapshot);
    if (snapshot == INVALID_HANDLE_VALUE) return 0;
    MODULEENTRY32W module{}; module.dwSize = sizeof(module);
    if (Module32FirstW(snapshot, &module)) do {
        if (_wcsicmp(fullPath ? module.szExePath : module.szModule, name.c_str()) == 0)
            return reinterpret_cast<uintptr_t>(module.modBaseAddr);
    } while (Module32NextW(snapshot, &module));
    return 0;
}
inline bool Inject(HANDLE process, const std::wstring& dllPath, std::wstring& error) {
    auto fail = [&](const wchar_t* stage) {
        error = std::wstring(stage) + L" (Windows error " + std::to_wstring(GetLastError()) + L")";
        return false;
    };
    // Call only for a suspended process with the same architecture as this caller.
    BOOL targetWow = FALSE, selfWow = FALSE;
    if (!IsWow64Process(process, &targetWow) || !IsWow64Process(GetCurrentProcess(), &selfWow))
        return fail(L"Cannot inspect process architecture");
    if (targetWow != selfWow) { error = L"Injection requires matching process architecture"; return false; }
    wchar_t absolute[32768];
    DWORD n = GetFullPathNameW(dllPath.c_str(), 32768, absolute, nullptr);
    if (!n || n >= 32768) return fail(L"Cannot resolve isolation DLL path");
    std::wstring resolved(absolute, n);
    HMODULE localDll = LoadLibraryExW(resolved.c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);
    if (!localDll) return fail(L"Cannot inspect isolation DLL");
    auto init = GetProcAddress(localDll, "MultiSeatInitialize");
    if (!init) { FreeLibrary(localDll); error = L"Isolation DLL is missing MultiSeatInitialize"; return false; }
    uintptr_t initOffset = reinterpret_cast<uintptr_t>(init) - reinterpret_cast<uintptr_t>(localDll);
    FreeLibrary(localDll);
    auto load = GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
    HMODULE loadModule = nullptr;
    if (!load || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, reinterpret_cast<LPCWSTR>(load), &loadModule))
        return fail(L"Cannot locate loader");
    wchar_t modulePath[32768];
    if (!GetModuleFileNameW(loadModule, modulePath, 32768)) return fail(L"Cannot locate loader module");
    std::wstring moduleName(modulePath);
    moduleName = moduleName.substr(moduleName.find_last_of(L"\\/") + 1);
    DWORD pid = GetProcessId(process);
    uintptr_t remoteModule = FindModule(pid, moduleName, false);
    if (!remoteModule) {
        // A newly suspended process may have only ntdll mapped. Creating its first
        // remote thread lets Windows initialize imports; the application's primary
        // thread remains suspended. Exit immediately once that loader work completes.
        HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        auto exitThread = GetProcAddress(ntdll, "RtlExitUserThread");
        uintptr_t remoteNtdll = FindModule(pid, L"ntdll.dll", false);
        if (!remoteNtdll) {
            // Toolhelp's loader list is empty before initialization. Query mapped
            // images directly instead; unlike a guessed address this handles ASLR.
            uintptr_t address = 0;
            MEMORY_BASIC_INFORMATION region{};
            while (VirtualQueryEx(process, reinterpret_cast<void*>(address), &region, sizeof(region))) {
                if (region.Type == MEM_IMAGE && region.BaseAddress == region.AllocationBase) {
                    wchar_t mapped[32768];
                    DWORD count = K32GetMappedFileNameW(process, region.AllocationBase, mapped, 32768);
                    if (count && count < 32768) {
                        std::wstring image(mapped, count);
                        auto name = image.substr(image.find_last_of(L"\\/") + 1);
                        if (_wcsicmp(name.c_str(), L"ntdll.dll") == 0) {
                            remoteNtdll = reinterpret_cast<uintptr_t>(region.AllocationBase); break;
                        }
                    }
                }
                uintptr_t next = reinterpret_cast<uintptr_t>(region.BaseAddress) + region.RegionSize;
                if (next <= address) break;
                address = next;
            }
        }
        if (!exitThread || !remoteNtdll) { error = L"Target bootstrap module unavailable"; return false; }
        uintptr_t remoteExit = remoteNtdll + reinterpret_cast<uintptr_t>(exitThread) - reinterpret_cast<uintptr_t>(ntdll);
        Handle bootstrap(CreateRemoteThread(process, nullptr, 0,
            reinterpret_cast<LPTHREAD_START_ROUTINE>(remoteExit), nullptr, 0, nullptr));
        if (!bootstrap.value) return fail(L"Cannot initialize target loader");
        if (WaitForSingleObject(bootstrap.value, 60000) != WAIT_OBJECT_0) {
            error = L"Target loader initialization failed or timed out"; return false;
        }
        remoteModule = FindModule(pid, moduleName, false);
    }
    if (!remoteModule) { error = L"Target loader module unavailable"; return false; }
    uintptr_t remoteLoad = remoteModule + reinterpret_cast<uintptr_t>(load) - reinterpret_cast<uintptr_t>(loadModule);
    SIZE_T bytes = (resolved.size() + 1) * sizeof(wchar_t);
    void* remotePath = VirtualAllocEx(process, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remotePath) return fail(L"Cannot allocate remote path");
    SIZE_T written = 0;
    if (!WriteProcessMemory(process, remotePath, resolved.c_str(), bytes, &written) || written != bytes) {
        auto result = fail(L"Cannot write remote path"); VirtualFreeEx(process, remotePath, 0, MEM_RELEASE); return result;
    }
    Handle loader(CreateRemoteThread(process, nullptr, 0, reinterpret_cast<LPTHREAD_START_ROUTINE>(remoteLoad), remotePath, 0, nullptr));
    if (!loader.value) { auto result = fail(L"Cannot start remote loader"); VirtualFreeEx(process, remotePath, 0, MEM_RELEASE); return result; }
    DWORD wait = WaitForSingleObject(loader.value, 60000);
    if (wait != WAIT_OBJECT_0) {
        // Leave the allocation alive until the caller terminates the failed suspended child.
        error = L"Remote loader failed or timed out"; return false;
    }
    VirtualFreeEx(process, remotePath, 0, MEM_RELEASE);
    uintptr_t remoteDll = FindModule(pid, resolved, true);
    if (!remoteDll) { error = L"Target did not load isolation DLL"; return false; }
    Handle initializer(CreateRemoteThread(process, nullptr, 0,
        reinterpret_cast<LPTHREAD_START_ROUTINE>(remoteDll + initOffset), nullptr, 0, nullptr));
    if (!initializer.value) return fail(L"Cannot start isolation initialization");
    if (WaitForSingleObject(initializer.value, 60000) != WAIT_OBJECT_0) {
        error = L"Isolation initialization failed or timed out"; return false;
    }
    DWORD result = 0;
    if (!GetExitCodeThread(initializer.value, &result) || result != 1) {
        error = L"Isolation initialization refused to activate"; return false;
    }
    return true;
}
}
