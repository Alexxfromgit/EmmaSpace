#include "emmascan/emmascan.h"

#include <catch2/catch_test_macros.hpp>

#include <chrono>
#include <filesystem>
#include <fstream>
#include <random>
#include <string>
#include <thread>

#if defined(_WIN32)
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  include <windows.h>
#else
#  include <fcntl.h>
#  include <unistd.h>
#endif

namespace fs = std::filesystem;

namespace {

struct TempTree {
    fs::path root;

    TempTree() {
        std::random_device rd;
        root = fs::temp_directory_path() / ("emmascan-test-" + std::to_string(rd()));
        fs::create_directories(root / "docs" / "deep" / "deeper");
        fs::create_directories(root / "empty");
        write(root / "top.bin", 5000);
        write(root / "docs" / "a.txt", 100);
        write(root / "docs" / "deep" / "b.txt", 200);
        write(root / "docs" / "deep" / "deeper" / u8"ünïcödé.dat", 300);
    }
    ~TempTree() {
        std::error_code ec;
        fs::remove_all(root, ec);
    }

    static void write(const fs::path& p, size_t bytes) {
        std::ofstream(p, std::ios::binary) << std::string(bytes, 'x');
    }

    std::u16string path16() const {
        auto w = root.wstring();
        return {w.begin(), w.end()};
    }
};

std::u16string_view name_of(const es_result& r, uint32_t node) {
    const auto& n = r.nodes[node];
    return {reinterpret_cast<const char16_t*>(r.names) + n.name_offset, n.name_length};
}

} // namespace

TEST_CASE("version matches the header") { CHECK(es_get_version() == ES_ABI_VERSION); }

TEST_CASE("storage kind of an existing folder is one of the known kinds") {
    TempTree tree;
    const auto path = tree.path16();
    uint32_t kind = 99;
    uint64_t device = 0;
    REQUIRE(es_get_storage_kind(reinterpret_cast<const es_char*>(path.c_str()), &kind, &device) == ES_OK);
    CHECK(kind <= ES_STORAGE_NETWORK);
    CHECK(device != 0);

    // Two folders of one file system report the same device.
    const auto w = (tree.root / "docs").wstring();
    const std::u16string docs(w.begin(), w.end());
    uint32_t docs_kind = 99;
    uint64_t docs_device = 0;
    REQUIRE(es_get_storage_kind(reinterpret_cast<const es_char*>(docs.c_str()), &docs_kind, &docs_device) == ES_OK);
    CHECK(docs_kind == kind);
    CHECK(docs_device == device);
}

TEST_CASE("storage kind of a missing path is unknown, not an error") {
    TempTree tree;
    const auto w = (tree.root / "does-not-exist" / "x").wstring();
    const std::u16string path(w.begin(), w.end());
    uint32_t kind = 99;
    // On Windows the volume of a missing path can still be told, so either answer is fine; it must not fail.
    REQUIRE(es_get_storage_kind(reinterpret_cast<const es_char*>(path.c_str()), &kind, nullptr) == ES_OK);
    CHECK(kind <= ES_STORAGE_NETWORK);
}

TEST_CASE("storage kind rejects missing arguments") {
    const std::u16string path = u"x";
    uint32_t kind = 99;
    CHECK(es_get_storage_kind(nullptr, &kind, nullptr) == ES_ERR_INVALID_ARG);
    CHECK(kind == ES_STORAGE_UNKNOWN);
    CHECK(es_get_storage_kind(reinterpret_cast<const es_char*>(u""), &kind, nullptr) == ES_ERR_INVALID_ARG);
    CHECK(es_get_storage_kind(reinterpret_cast<const es_char*>(path.c_str()), nullptr, nullptr) == ES_ERR_INVALID_ARG);
}

namespace {

// Opens a file read-only the way the app does and returns its handle as the API takes it.
struct ReadOnlyFile {
#if defined(_WIN32)
    HANDLE handle;
    explicit ReadOnlyFile(const fs::path& p)
        : handle(CreateFileW(p.wstring().c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                             nullptr, OPEN_EXISTING, 0, nullptr)) {}
    ~ReadOnlyFile() {
        if (handle != INVALID_HANDLE_VALUE) CloseHandle(handle);
    }
    intptr_t value() const { return reinterpret_cast<intptr_t>(handle); }
#else
    int fd;
    explicit ReadOnlyFile(const fs::path& p) : fd(open(p.c_str(), O_RDONLY)) {}
    ~ReadOnlyFile() {
        if (fd >= 0) close(fd);
    }
    intptr_t value() const { return fd; }
#endif
};

es_file_stamp stamp_of(const fs::path& p) {
    ReadOnlyFile file(p);
    es_file_stamp stamp{};
    REQUIRE(es_get_file_stamp(file.value(), &stamp) == ES_OK);
    return stamp;
}

} // namespace

TEST_CASE("file stamp identifies a file and sees its changes") {
    TempTree tree;
    const auto top = tree.root / "top.bin";
    const auto before = stamp_of(top);
    CHECK(before.size == 5000);
    CHECK((before.file_id_low != 0 || before.file_id_high != 0));
    CHECK(before.volume != 0);
    CHECK(before.mtime_ns > 0);
    CHECK(before.ctime_ns > 0);

    const auto other = stamp_of(tree.root / "docs" / "a.txt");
    CHECK(other.volume == before.volume);
    CHECK((other.file_id_low != before.file_id_low || other.file_id_high != before.file_id_high));

    // Rewritten with the same size and its last-write time put back: the change time still moves on.
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
    TempTree::write(top, 5000);
    const auto mtime = fs::last_write_time(top);
    fs::last_write_time(top, mtime - std::chrono::hours(1));
    fs::last_write_time(top, mtime);
    const auto after = stamp_of(top);
    CHECK(after.size == before.size);
    CHECK(after.file_id_low == before.file_id_low);
    CHECK(after.ctime_ns != before.ctime_ns);
#if defined(_WIN32)
    // The USN moves on too, where the volume keeps a change journal (the system drive does).
    if (before.usn != 0) {
        CHECK(after.usn != before.usn);
        CHECK((before.flags & ES_STAMP_TRUSTED) != 0);
    } else {
        CHECK((before.flags & ES_STAMP_TRUSTED) == 0);
    }
#else
    CHECK((before.flags & ES_STAMP_TRUSTED) != 0);
    CHECK(before.usn == 0);
#endif
}

TEST_CASE("file stamp rejects a bad handle or missing output") {
    es_file_stamp stamp{};
#if defined(_WIN32)
    CHECK(es_get_file_stamp(reinterpret_cast<intptr_t>(INVALID_HANDLE_VALUE), &stamp) == ES_ERR_INVALID_ARG);
#else
    CHECK(es_get_file_stamp(-1, &stamp) == ES_ERR_INVALID_ARG);
#endif
    TempTree tree;
    ReadOnlyFile file(tree.root / "top.bin");
    CHECK(es_get_file_stamp(file.value(), nullptr) == ES_ERR_INVALID_ARG);
}

TEST_CASE("scan produces a correct tree for a small folder") {
    TempTree tree;
    const auto path = tree.path16();

    es_scan_options options{};
    options.struct_size = sizeof(options);
    options.mode = ES_MODE_WALKER;
    options.root_path = reinterpret_cast<const es_char*>(path.c_str());

    es_scan* scan = nullptr;
    REQUIRE(es_scan_start(&options, &scan) == ES_OK);
    REQUIRE(es_scan_wait(scan, 30000) == ES_OK);

    es_result result{};
    REQUIRE(es_scan_get_result(scan, &result) == ES_OK);
    const es_node& root = result.nodes[0];
    CHECK(root.parent == ES_NONE);
    CHECK(root.size == 5600);
    CHECK(root.file_count == 4);
    CHECK(root.dir_count == 4);
    CHECK(result.node_count == 9);
    CHECK(result.errors == 0);

    // Largest child first: top.bin (5000) before docs (600) before empty (0).
    const uint32_t first = root.first_child;
    CHECK(name_of(result, first) == u"top.bin");
    CHECK(name_of(result, result.nodes[first].next_sibling) == u"docs");

    bool found_unicode = false;
    for (uint32_t i = 0; i < result.node_count; ++i)
        if (name_of(result, i) == u"ünïcödé.dat") found_unicode = true;
    CHECK(found_unicode);

    es_progress progress{};
    es_char buffer[260];
    REQUIRE(es_scan_get_progress(scan, &progress, buffer, 260) == ES_OK);
    CHECK(progress.phase == ES_PHASE_DONE);
    CHECK(progress.files == 4);
    es_scan_free(scan);
}

#if !defined(_WIN32)
TEST_CASE("POSIX scan counts hard links once and does not follow symlinks") {
    TempTree tree;
    fs::create_hard_link(tree.root / "top.bin", tree.root / "docs" / "top-link.bin");
    fs::create_directory_symlink(tree.root / "docs", tree.root / "docs-link");
    fs::create_symlink(tree.root / "top.bin", tree.root / "file-link");
    const auto path = tree.path16();

    es_scan_options options{};
    options.struct_size = sizeof(options);
    options.root_path = reinterpret_cast<const es_char*>(path.c_str());
    es_scan* scan = nullptr;
    REQUIRE(es_scan_start(&options, &scan) == ES_OK);
    REQUIRE(es_scan_wait(scan, 30000) == ES_OK);
    es_result result{};
    REQUIRE(es_scan_get_result(scan, &result) == ES_OK);

    uint32_t hard_links = 0;
    uint64_t link_size = 0;
    bool docs_link_is_file = false;
    for (uint32_t i = 0; i < result.node_count; ++i) {
        const auto& n = result.nodes[i];
        if (n.flags & ES_NODE_HARDLINK) {
            ++hard_links;
            link_size += n.size;
        }
        if (name_of(result, i) == u"docs-link") docs_link_is_file = !(n.flags & ES_NODE_DIRECTORY);
        if (name_of(result, i) == u"docs-link" || name_of(result, i) == u"file-link")
            CHECK((n.flags & ES_NODE_REPARSE) != 0); // symlinks are flagged so nothing follows them
    }
    CHECK(hard_links == 1);          // one of the two names of top.bin
    CHECK(link_size == 0);
    CHECK(docs_link_is_file);        // a symlink is recorded as itself, never descended
    // Original 5600 bytes plus the two symlinks' own (tiny) sizes; top.bin is counted once.
    CHECK(result.nodes[0].size < 5600 + 1024);
    CHECK(result.nodes[0].size >= 5600);
    CHECK(result.nodes[0].dir_count == 4);
    es_scan_free(scan);
}
#endif

TEST_CASE("scan of a missing path fails cleanly") {
    const std::u16string path = u"Z:\\definitely\\not\\here\\emmascan";
    es_scan_options options{};
    options.struct_size = sizeof(options);
    options.root_path = reinterpret_cast<const es_char*>(path.c_str());
    es_scan* scan = nullptr;
    REQUIRE(es_scan_start(&options, &scan) == ES_OK);
    CHECK(es_scan_wait(scan, 30000) == ES_ERR_NOT_FOUND);
    es_result result{};
    CHECK(es_scan_get_result(scan, &result) == ES_ERR_NOT_FOUND);
    es_scan_free(scan);
}

TEST_CASE("invalid arguments are rejected") {
    es_scan* scan = nullptr;
    CHECK(es_scan_start(nullptr, &scan) == ES_ERR_INVALID_ARG);
    es_scan_options options{};
    options.struct_size = sizeof(options);
    CHECK(es_scan_start(&options, &scan) == ES_ERR_INVALID_ARG);
    CHECK(es_move_to_trash(nullptr) == ES_ERR_INVALID_ARG);
}

// Hidden by default because it leaves a file in the real Recycle Bin. Run with: emmascan_tests [trash]
TEST_CASE("move_to_trash removes a file", "[.][trash]") {
    TempTree tree;
    const auto file = tree.root / "top.bin";
    auto w = file.wstring();
    std::u16string p(w.begin(), w.end());
    REQUIRE(es_move_to_trash(reinterpret_cast<const es_char*>(p.c_str())) == ES_OK);
    CHECK_FALSE(fs::exists(file));
}
