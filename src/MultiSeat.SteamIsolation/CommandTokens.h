#pragma once
#include <string>

namespace steam_isolation {

// Decode only the launcher prefix of a command line. The application's argument tail remains
// byte-for-byte intact: callers keep the raw text after the cursor, not a re-quoted copy of it.
// Shared by the launcher and the shim so that both read `--steam <path> [flags] -- <tail>` the same way.
inline bool NextToken(const std::wstring& raw, size_t& cursor, std::wstring& token) {
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

}  // namespace steam_isolation
