#include "emmascan/emmascan.h"

#include <catch2/catch_test_macros.hpp>

#include <chrono>
#include <filesystem>
#include <fstream>
#include <map>
#include <random>
#include <string>
#include <vector>

#if !defined(_WIN32)
#include <sys/stat.h>
#include <unistd.h>
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

#if !defined(_WIN32)
namespace {

// Relative path ("a/b/c") of every node below the root.
std::map<std::string, uint32_t> paths_of(const es_result& r) {
    std::map<std::string, uint32_t> out;
    std::vector<std::string> path_of(r.node_count);
    for (uint32_t i = 1; i < r.node_count; ++i) { // parents always come before their children
        const auto name = name_of(r, i);
        std::string utf8 = fs::path(std::u16string(name)).string();
        const uint32_t parent = r.nodes[i].parent;
        path_of[i] = parent == 0 ? utf8 : path_of[parent] + "/" + utf8;
        out[path_of[i]] = i;
    }
    return out;
}

} // namespace

// Checks the walker against lstat() done here, so it is not compared with its own code. On macOS this
// covers getattrlistbulk; elsewhere readdir + fstatat.
TEST_CASE("POSIX scan matches lstat for every entry") {
    TempTree tree;
    const fs::path& root = tree.root;
    // Enough entries with long names to need several getattrlistbulk batches.
    fs::create_directories(root / "many");
    for (int i = 0; i < 6000; ++i)
        TempTree::write(root / "many" / ("file-with-a-fairly-long-name-to-fill-the-batch-buffer-" + std::to_string(i)),
                        static_cast<size_t>(i % 97));
    TempTree::write(root / std::string(255, 'n'), 10); // longest name APFS and ext4 allow
    TempTree::write(root / "e\xcc\x81-decomposed.txt", 11); // "é" as e + combining acute (NFD)
    TempTree::write(root / "\xf0\x9f\x98\x80.txt", 12);   // emoji outside the BMP
    fs::path deep = root;
    for (int i = 0; i < 40; ++i) deep /= "d" + std::to_string(i);
    fs::create_directories(deep);
    TempTree::write(deep / "bottom.txt", 13);
    {
        std::ofstream sparse(root / "sparse.bin", std::ios::binary);
        sparse.seekp(64 * 1024 * 1024 - 1);
        sparse.put('x'); // 64 MiB long, almost nothing allocated (on file systems with holes)
    }
    fs::create_hard_link(root / "top.bin", root / "docs" / "top-link.bin");
    fs::create_symlink("top.bin", root / "file-link");
    fs::create_directory_symlink("docs", root / "dir-link");

    const auto path = tree.path16();
    es_scan_options options{};
    options.struct_size = sizeof(options);
    options.mode = ES_MODE_WALKER;
    options.root_path = reinterpret_cast<const es_char*>(path.c_str());
    es_scan* scan = nullptr;
    REQUIRE(es_scan_start(&options, &scan) == ES_OK);
    REQUIRE(es_scan_wait(scan, 60000) == ES_OK);
    es_result result{};
    REQUIRE(es_scan_get_result(scan, &result) == ES_OK);
    CHECK(result.errors == 0);
    const auto nodes = paths_of(result);

    size_t expected = 0;
    int hard_link_names_counted = 0;
    for (auto it = fs::recursive_directory_iterator(root); it != fs::recursive_directory_iterator(); ++it) {
        ++expected;
        const std::string rel = it->path().lexically_relative(root).string();
        INFO(rel);
        struct stat st{};
        REQUIRE(lstat(it->path().c_str(), &st) == 0);
        const auto found = nodes.find(rel);
        REQUIRE(found != nodes.end());
        const es_node& n = result.nodes[found->second];
        CHECK(((n.flags & ES_NODE_DIRECTORY) != 0) == S_ISDIR(st.st_mode));
        CHECK(n.attributes == static_cast<uint32_t>(st.st_mode));
#if defined(__APPLE__)
        const auto& mt = st.st_mtimespec;
#else
        const auto& mt = st.st_mtim;
#endif
        CHECK(n.mtime == static_cast<int64_t>(mt.tv_sec) * 1000 + mt.tv_nsec / 1000000);
        if (S_ISLNK(st.st_mode)) {
            CHECK((n.flags & ES_NODE_REPARSE) != 0);
            CHECK(n.first_child == ES_NONE); // never followed
        }
        if (!S_ISDIR(st.st_mode)) {
            if (st.st_nlink > 1) {
                if (n.size != 0) ++hard_link_names_counted;
            } else {
                CHECK(n.size == static_cast<uint64_t>(st.st_size));
                CHECK(n.allocated == static_cast<uint64_t>(st.st_blocks) * 512);
            }
        }
    }
    CHECK(hard_link_names_counted == 1); // top.bin and top-link.bin share one count
    CHECK(nodes.size() == expected);     // nothing extra either
    CHECK(result.nodes[0].file_count == expected - result.nodes[0].dir_count);
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
