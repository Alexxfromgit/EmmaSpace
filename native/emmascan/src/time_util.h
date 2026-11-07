#pragma once

#include <algorithm>
#include <cstdint>

namespace emmascan {

// Timestamps are Unix epoch milliseconds. File systems allow dates far outside what consumers can
// show (NTFS goes up to the year 30828), so they are clamped to years 0001-9999, the range .NET's
// DateTimeOffset supports.
constexpr int64_t kMinUnixMs = -62135596800000LL;
constexpr int64_t kMaxUnixMs = 253402300799999LL;

inline int64_t clamp_unix_ms(int64_t ms) { return std::clamp(ms, kMinUnixMs, kMaxUnixMs); }

} // namespace emmascan
