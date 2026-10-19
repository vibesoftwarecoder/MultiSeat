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
// Is this NT key path Steam's machine-wide key, in either registry view? Steam keeps SteamPID and
// TempAppCmdLine there, so every Steam on the host shares them: a second steam.exe reads SteamPID, and
// if that process is alive it writes its command line to TempAppCmdLine and exits. Shared across
// accounts, that hands a seat's launch to the console's Steam. The hook moves both values to the user's
// own HKCU key, which keeps accounts apart and still lets two launches in one account find each other.
inline bool IsMachineSteamKeyPath(const std::wstring& ntPath) {
    static const wchar_t* const accepted[] = {
        L"\\REGISTRY\\MACHINE\\SOFTWARE\\VALVE\\STEAM",
        L"\\REGISTRY\\MACHINE\\SOFTWARE\\WOW6432NODE\\VALVE\\STEAM"};
    std::wstring upper = ntPath;
    std::transform(upper.begin(), upper.end(), upper.begin(), towupper);
    for (const wchar_t* path : accepted) if (upper == path) return true;
    return false;
}
inline bool RegularFileExists(const std::wstring& path) {
    const DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY);
}
using FileExists = bool (*)(const std::wstring&);
struct ImageSplit { std::wstring image, rest; };
// Where the program name ends in a command line, the way CreateProcess finds it when it is given
// no application name. A quoted first token ends at its closing quote. An unquoted one may contain
// spaces ("C:\Program Files (x86)\Steam\steam.exe -silent"): CreateProcess tries the text up to
// each space in turn until a file exists. CommandLineToArgvW does not, and reads that example as
// "C:\Program", which is not Steam, so a restart written that way was neither rewritten nor given
// the hook. Only the two programs this component acts on are searched for; anything else comes
// back as its first word, which is all the caller needs to know it is not one of them.
//
// The first word is taken on its name alone: "steam.exe -silent" is found through the search path,
// so it need not exist where we stand. A longer candidate must exist on disk, so that a folder
// that merely contains a space and the word steam.exe is not mistaken for the program.
inline ImageSplit SplitImage(const std::wstring& command, FileExists exists = RegularFileExists) {
    auto blank = [](wchar_t c) { return c == L' ' || c == L'\t'; };
    size_t start = 0;
    while (start < command.size() && blank(command[start])) ++start;
    if (start == command.size()) return {};
    if (command[start] == L'"') {
        const size_t close = command.find(L'"', start + 1);
        if (close == std::wstring::npos) return {command.substr(start + 1), {}};
        return {command.substr(start + 1, close - start - 1), command.substr(close + 1)};
    }
    size_t firstEnd = start;
    while (firstEnd < command.size() && !blank(command[firstEnd])) ++firstEnd;
    for (size_t end = firstEnd;;) {
        const std::wstring candidate = command.substr(start, end - start);
        if ((SteamImage(candidate) || BrowserImage(candidate)) && (end == firstEnd || exists(candidate)))
            return {candidate, command.substr(end)};
        size_t next = end;
        while (next < command.size() && blank(command[next])) ++next;
        if (next >= command.size()) break;
        while (next < command.size() && !blank(command[next])) ++next;
        end = next;
    }
    return {command.substr(start, firstEnd - start), command.substr(firstEnd)};
}
inline std::wstring RewriteCommand(const std::wstring& raw, const std::wstring& name,
                                   FileExists exists = RegularFileExists) {
    const ImageSplit split = SplitImage(raw, exists);
    if (split.image.empty()) return raw;
    // CommandLineToArgvW reads its first token by special rules, so give it a placeholder and
    // take the arguments from what follows; the real program name comes from SplitImage.
    int count = 0; LPWSTR* parsed = CommandLineToArgvW((L"x" + split.rest).c_str(), &count);
    if (!parsed || count == 0) { if (parsed) LocalFree(parsed); return raw; }
    std::vector<std::wstring> args(parsed, parsed + count); LocalFree(parsed);
    args[0] = split.image;
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
    // `characters` is the size of the buffer the caller passed, and a caller may pass more than the block
    // (CreateProcess does, for games with a long environment). The block ends at its own empty entry, so
    // that is where reading stops; only an entry that runs off the buffer, or no terminator, is malformed.
    if (!data || characters < 1) return {};
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
    if (pos >= characters) return {};  // ran out of buffer before the block's empty terminating entry
    entries.push_back(L"steam_master_ipc_name_override=" + name);
    std::sort(entries.begin(), entries.end(), [](const auto& a, const auto& b) { return _wcsicmp(a.c_str(), b.c_str()) < 0; });
    std::vector<wchar_t> result;
    for (const auto& value : entries) { result.insert(result.end(), value.begin(), value.end()); result.push_back(0); }
    result.push_back(0); return result;
}
}
