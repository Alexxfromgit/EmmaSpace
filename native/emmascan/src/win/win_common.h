#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include "emmascan/emmascan.h"
#include "../time_util.h"

#include <cstdint>
#include <string>
#include <string_view>

struct es_scan;

namespace emmascan::win {

class UniqueHandle {
public:
    explicit UniqueHandle(HANDLE h = INVALID_HANDLE_VALUE) : h_(h) {}
    ~UniqueHandle() { reset(); }
    UniqueHandle(const UniqueHandle&) = delete;
    UniqueHandle& operator=(const UniqueHandle&) = delete;

    bool valid() const { return h_ != INVALID_HANDLE_VALUE && h_ != nullptr; }
    HANDLE get() const { return h_; }
    void reset() {
        if (valid()) CloseHandle(h_);
        h_ = INVALID_HANDLE_VALUE;
    }

private:
    HANDLE h_;
};

// Files whose data may live only in the cloud (OneDrive and other cloud-files providers).
inline bool is_cloud_placeholder(DWORD attributes) {
    return (attributes & (FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | FILE_ATTRIBUTE_RECALL_ON_OPEN | FILE_ATTRIBUTE_OFFLINE)) != 0;
}

// Windows FILETIME (100 ns since 1601) to Unix epoch milliseconds.
inline int64_t filetime_to_unix_ms(int64_t ft) {
    constexpr int64_t epoch_diff = 116444736000000000LL;
    return ft == 0 ? 0 : clamp_unix_ms((ft - epoch_diff) / 10000);
}

inline int64_t filetime_to_unix_ms(const FILETIME& ft) {
    return filetime_to_unix_ms(static_cast<int64_t>((static_cast<uint64_t>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime));
}

// Adds the \\?\ prefix so paths longer than MAX_PATH work.
inline std::wstring to_long_path(const std::wstring& path) {
    if (path.rfind(L"\\\\?\\", 0) == 0) return path;
    if (path.rfind(L"\\\\", 0) == 0) return L"\\\\?\\UNC\\" + path.substr(2);
    return L"\\\\?\\" + path;
}

// Normalizes a user supplied root: forward slashes become backslashes, trailing separators
// are dropped except for a drive root, which keeps exactly one ("C:\").
inline std::wstring normalize_root(std::wstring_view input) {
    std::wstring path(input);
    for (auto& c : path)
        if (c == L'/') c = L'\\';
    while (path.size() > 1 && path.back() == L'\\') path.pop_back();
    if (path.size() == 2 && path[1] == L':') path.push_back(L'\\');
    return path;
}

inline es_status status_from_win32(DWORD err) {
    switch (err) {
    case ERROR_FILE_NOT_FOUND:
    case ERROR_PATH_NOT_FOUND:
    case ERROR_INVALID_DRIVE:
    case ERROR_BAD_NETPATH:
        return ES_ERR_NOT_FOUND;
    case ERROR_ACCESS_DENIED:
        return ES_ERR_ACCESS_DENIED;
    case ERROR_INVALID_NAME:
    case ERROR_INVALID_PARAMETER:
        return ES_ERR_INVALID_ARG;
    case ERROR_NOT_ENOUGH_MEMORY:
    case ERROR_OUTOFMEMORY:
        return ES_ERR_NO_MEMORY;
    default:
        return ES_ERR_IO;
    }
}

es_status walk(es_scan& scan, const std::wstring& root_path);

es_status mft_scan(es_scan& scan, const std::wstring& root_path);

inline bool is_volume_root(const std::wstring& path) {
    wchar_t volume[MAX_PATH];
    if (!GetVolumePathNameW(path.c_str(), volume, MAX_PATH)) return false;
    std::wstring v(volume), p(path);
    while (v.size() > 1 && v.back() == L'\\') v.pop_back();
    while (p.size() > 1 && p.back() == L'\\') p.pop_back();
    return CompareStringOrdinal(v.c_str(), -1, p.c_str(), -1, TRUE) == CSTR_EQUAL;
}

} // namespace emmascan::win
