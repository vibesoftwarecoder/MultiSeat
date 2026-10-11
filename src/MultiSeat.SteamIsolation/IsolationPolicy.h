#pragma once
#include <windows.h>
#include <shellapi.h>
#include <string>
#include <vector>
#include <algorithm>
#include <cwctype>

namespace steam_isolation {
inline std::wstring BaseName(const std::wstring& path) {
    return path.substr(path.find_last_of(L"\\/") + 1);
}
inline bool Equal(const std::wstring& a, const std::wstring& b) { return _wcsicmp(a.c_str(), b.c_str()) == 0; }
inline bool SteamImage(const std::wstring& image) { return Equal(BaseName(image), L"steam.exe"); }
inline bool BrowserImage(const std::wstring& image) { return Equal(BaseName(image), L"steamwebhelper.exe"); }
inline std::wstring Quote(const std::wstring& value) {
    std::wstring result = L"\""; size_t slashes = 0;
    for (wchar_t c : value) {
        if (c == L'\\') { ++slashes; continue; }
        if (c == L'"') result.append(slashes * 2 + 1, L'\\');
        else result.append(slashes, L'\\');
        slashes = 0; result += c;
    }
    result.append(slashes * 2, L'\\'); return result + L"\"";
}
inline std::wstring RewriteCommand(const std::wstring& raw, const std::wstring& name) {
    int count = 0; LPWSTR* parsed = CommandLineToArgvW(raw.c_str(), &count);
    if (!parsed || count == 0) { if (parsed) LocalFree(parsed); return raw; }
    std::vector<std::wstring> args(parsed, parsed + count); LocalFree(parsed);
    if (!SteamImage(args[0]) && !BrowserImage(args[0])) return raw;
    std::vector<std::wstring> output{args[0]}; bool changed = false;
    for (size_t i = 1; i < args.size(); ++i) {
        auto arg = args[i];
        if (SteamImage(args[0]) && Equal(arg, L"-master_ipc_name_override")) {
            if (i + 1 < args.size()) ++i;
            changed = true; continue;
        }
        if (SteamImage(args[0]) && arg.size() >= 26 && Equal(arg.substr(0, 26), L"-master_ipc_name_override=")) {
            changed = true; continue;
        }
        std::wstring lowerArg = arg; std::transform(lowerArg.begin(), lowerArg.end(), lowerArg.begin(), towlower);
        bool cachePath = lowerArg.rfind(L"-cachedir=", 0) == 0 || lowerArg.rfind(L"--log-file=", 0) == 0 ||
            lowerArg.rfind(L"-cef-log-file=", 0) == 0 || (arg.size() >= 3 && arg[1] == L':' && (arg[2] == L'\\' || arg[2] == L'/')) ||
            arg.rfind(L"\\\\", 0) == 0;
        // Limit rewriting to path arguments; URLs and arbitrary labels keep their values.
        for (const auto& marker : {std::wstring(L"htmlcache"), std::wstring(L"cef_log")}) {
            if (!cachePath) break;
            std::wstring lower = arg; std::transform(lower.begin(), lower.end(), lower.begin(), towlower);
            size_t position = 0;
            while ((position = lower.find(marker, position)) != std::wstring::npos) {
                size_t end = position + marker.size();
                bool left = position == 0 || arg[position - 1] == L'\\' || arg[position - 1] == L'/' || arg[position - 1] == L'=';
                bool right = end == arg.size() || arg[end] == L'.' || arg[end] == L'\\' || arg[end] == L'/';
                const std::wstring suffix = L"." + name;
                if (left && right && arg.compare(end, suffix.size(), suffix) != 0) {
                    arg.insert(end, suffix); lower.insert(end, suffix); changed = true; end += suffix.size();
                }
                position = end;
            }
        }
        output.push_back(std::move(arg));
    }
    if (SteamImage(args[0])) { output.push_back(L"-master_ipc_name_override"); output.push_back(name); changed = true; }
    if (!changed) return raw;
    std::wstring result;
    for (const auto& arg : output) { if (!result.empty()) result += L' '; result += Quote(arg); }
    return result;
}
inline std::vector<wchar_t> RewriteEnvironment(const wchar_t* data, size_t characters, const std::wstring& name) {
    if (!data || characters < 2 || data[characters - 1] != 0 || data[characters - 2] != 0) return {};
    std::vector<std::wstring> entries;
    size_t pos = 0;
    while (pos < characters && data[pos]) {
        size_t end = pos; while (end < characters && data[end]) ++end;
        if (end == characters) return {};
        std::wstring value(data + pos, end - pos);
        const std::wstring prefix = L"steam_master_ipc_name_override=";
        if (value.size() < prefix.size() || !Equal(value.substr(0, prefix.size()), prefix)) entries.push_back(value);
        pos = end + 1;
    }
    entries.push_back(L"steam_master_ipc_name_override=" + name);
    std::sort(entries.begin(), entries.end(), [](const auto& a, const auto& b) { return _wcsicmp(a.c_str(), b.c_str()) < 0; });
    std::vector<wchar_t> result;
    for (const auto& value : entries) { result.insert(result.end(), value.begin(), value.end()); result.push_back(0); }
    result.push_back(0); return result;
}
}
