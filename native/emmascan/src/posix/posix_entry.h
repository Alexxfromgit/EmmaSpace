#pragma once
// Turns one fstatat() result into an EntryInfo. Kept separate from the walker so tests can
// feed it synthetic stat data (e.g. iCloud "dataless" entries, which only the system can create).

#include "../node_store.h"
#include "../time_util.h"

#include <sys/stat.h>

#include <algorithm>
#include <cstdint>

namespace emmascan::platform {

#if defined(__APPLE__)
#  ifndef SF_DATALESS
#    define SF_DATALESS 0x40000000u
#  endif
// Online-only iCloud Drive / File Provider item (OneDrive, Dropbox, ...): its data, or for a
// folder its listing, lives in the cloud. Reading it would make the system download it.
inline bool is_dataless(const struct stat& st) { return (st.st_flags & SF_DATALESS) != 0; }
inline const struct timespec& mtime_of(const struct stat& st) { return st.st_mtimespec; }
inline const struct timespec& atime_of(const struct stat& st) { return st.st_atimespec; }
#else
inline bool is_dataless(const struct stat&) { return false; }
inline const struct timespec& mtime_of(const struct stat& st) { return st.st_mtim; }
inline const struct timespec& atime_of(const struct stat& st) { return st.st_atim; }
#endif

inline int64_t to_unix_ms(const struct timespec& t) {
    // Decide out-of-range seconds before multiplying, so absurd dates cannot overflow.
    const auto sec = static_cast<int64_t>(t.tv_sec);
    if (sec > kMaxUnixMs / 1000) return kMaxUnixMs;
    if (sec < kMinUnixMs / 1000) return kMinUnixMs;
    return clamp_unix_ms(sec * 1000 + t.tv_nsec / 1000000);
}

// Fills sizes, times, attributes and flags (DIRECTORY, CLOUD, REPARSE for symlinks). Hard-link and firmlink
// de-duplication need walker state and are applied by the caller.
// A directory flagged ES_NODE_CLOUD must not be listed.
inline EntryInfo describe_entry(const struct stat& st) {
    EntryInfo e;
    e.mtime = to_unix_ms(mtime_of(st));
    e.atime = to_unix_ms(atime_of(st));
    e.attributes = static_cast<uint32_t>(st.st_mode);
    const bool cloud = is_dataless(st);
    if (S_ISDIR(st.st_mode)) {
        e.flags = ES_NODE_DIRECTORY;
    } else {
        e.size = static_cast<uint64_t>(st.st_size);
        e.allocated = static_cast<uint64_t>(st.st_blocks) * 512;
        if (cloud) e.size = std::min(e.size, e.allocated); // online-only data takes no local space
        // A symlink is recorded as itself and flagged, so nothing (e.g. duplicate hashing) follows it.
        if (S_ISLNK(st.st_mode)) e.flags = ES_NODE_REPARSE;
    }
    if (cloud) e.flags |= ES_NODE_CLOUD;
    return e;
}

} // namespace emmascan::platform
