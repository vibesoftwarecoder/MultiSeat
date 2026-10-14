#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <cstdio>

namespace steam_isolation {

// UTF-8 bytes of a wide string, for files and redirected handles. Empty on failure.
inline std::string Utf8(const std::wstring& text) {
    if (text.empty()) return {};
    const int bytes = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()),
                                          nullptr, 0, nullptr, nullptr);
    if (bytes <= 0) return {};
    std::string result(static_cast<size_t>(bytes), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()),
                        result.data(), bytes, nullptr, nullptr);
    return result;
}

// Records one line in %LOCALAPPDATA%\MultiSeat\SteamIsolation\launcher.log, in the profile of the
// user the calling process runs as, and sends it to the debugger. Used by the launcher, which has
// no console, and by the hook inside Steam, which has nowhere else to say that a restart was or
// was not isolated. The file restarts after 256 KB instead of growing for ever. Never throws and
// never fails the caller: logging is evidence, not a dependency.
inline void AppendLog(const std::wstring& message) {
    std::wstring text = message;
    while (!text.empty() && (text.back() == L'\n' || text.back() == L'\r')) text.pop_back();
    OutputDebugStringW((L"MultiSeat.SteamIsolation: " + text + L"\n").c_str());

    try {
        SYSTEMTIME now{}; GetLocalTime(&now);
        wchar_t stamp[40];
        swprintf_s(stamp, L"%04u-%02u-%02u %02u:%02u:%02u [pid %lu] ", now.wYear, now.wMonth, now.wDay,
                   now.wHour, now.wMinute, now.wSecond, GetCurrentProcessId());
        const std::string line = Utf8(stamp + text + L"\r\n");
        if (line.empty()) return;

        wchar_t base[MAX_PATH]{};
        const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", base, MAX_PATH);
        if (!length || length >= MAX_PATH) return;
        std::wstring directory = std::wstring(base, length) + L"\\MultiSeat";
        CreateDirectoryW(directory.c_str(), nullptr);
        directory += L"\\SteamIsolation";
        CreateDirectoryW(directory.c_str(), nullptr);

        HANDLE file = CreateFileW((directory + L"\\launcher.log").c_str(), FILE_APPEND_DATA | FILE_GENERIC_WRITE,
                                  FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS,
                                  FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return;
        LARGE_INTEGER size{};
        if (GetFileSizeEx(file, &size) && size.QuadPart > 256 * 1024) {
            SetFilePointer(file, 0, nullptr, FILE_BEGIN);
            SetEndOfFile(file);
        }
        SetFilePointer(file, 0, nullptr, FILE_END);
        DWORD written = 0;
        WriteFile(file, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
        CloseHandle(file);
    } catch (...) {
    }
}

}  // namespace steam_isolation
