// describe_entry() on synthetic stat data. Real online-only (dataless) entries can only be created
// by iCloud / File Provider, so their handling is checked here rather than on disk.

#include "posix/posix_entry.h"

#include <catch2/catch_test_macros.hpp>

using namespace emmascan::platform;

namespace {
struct stat make_stat(mode_t mode, off_t size, blkcnt_t blocks, uint32_t st_flags = 0) {
    struct stat st{};
    st.st_mode = mode;
    st.st_size = size;
    st.st_blocks = blocks;
#if defined(__APPLE__)
    st.st_flags = st_flags;
#else
    (void)st_flags;
#endif
    return st;
}
} // namespace

TEST_CASE("a regular file reports logical and allocated size") {
    const auto e = describe_entry(make_stat(S_IFREG | 0644, 5000, 16));
    CHECK(e.size == 5000);
    CHECK(e.allocated == 16 * 512);
    CHECK(e.flags == 0);
}

TEST_CASE("a symlink is flagged so nothing follows it") {
    const auto e = describe_entry(make_stat(S_IFLNK | 0777, 6, 0));
    CHECK(e.flags == ES_NODE_REPARSE);
    CHECK(e.size == 6); // the link itself, not its target
}

TEST_CASE("a directory has no own size") {
    const auto e = describe_entry(make_stat(S_IFDIR | 0755, 4096, 8));
    CHECK(e.size == 0);
    CHECK(e.allocated == 0);
    CHECK(e.flags == ES_NODE_DIRECTORY);
}

#if defined(__APPLE__)
TEST_CASE("an online-only iCloud file counts only its local bytes") {
    const auto e = describe_entry(make_stat(S_IFREG | 0644, 2'000'000'000, 0, SF_DATALESS));
    CHECK(e.size == 0);
    CHECK(e.allocated == 0);
    CHECK(e.flags == ES_NODE_CLOUD);
}

TEST_CASE("a partly downloaded cloud file counts what is on disk") {
    const auto e = describe_entry(make_stat(S_IFREG | 0644, 1'000'000, 256, SF_DATALESS));
    CHECK(e.size == 256 * 512);
    CHECK((e.flags & ES_NODE_CLOUD) != 0);
}

TEST_CASE("an online-only folder is flagged so the walker does not list it") {
    const auto e = describe_entry(make_stat(S_IFDIR | 0755, 0, 0, SF_DATALESS));
    CHECK(e.flags == (ES_NODE_DIRECTORY | ES_NODE_CLOUD));
}

TEST_CASE("other file flags are not mistaken for cloud items") {
    const auto e = describe_entry(make_stat(S_IFREG | 0644, 10, 8, UF_HIDDEN | UF_COMPRESSED));
    CHECK(e.size == 10);
    CHECK(e.flags == 0);
}
#endif

TEST_CASE("absurd timestamps are clamped instead of overflowing") {
    struct timespec far{};
    far.tv_sec = static_cast<time_t>(INT64_MAX / 2);
    CHECK(to_unix_ms(far) == emmascan::kMaxUnixMs);
    struct timespec normal{};
    normal.tv_sec = 1704067200; // 2024-01-01
    CHECK(to_unix_ms(normal) == 1704067200000LL);
}
