// ext4 metadata reader. Synthetic structures run everywhere; on Linux, real images made by mke2fs
// (no root needed) are compared with what the walker reports for the same files. As root, a loop-mounted
// image is also scanned through the public API in ES_MODE_EXT4 and compared exactly with the walker.

#include "emmascan/emmascan.h"
#include "ext4/ext4_parser.h"
#include "utf8.h"

#include <catch2/catch_test_macros.hpp>

#include <atomic>
#include <cstring>
#include <iterator>
#include <map>
#include <random>
#include <string>
#include <tuple>
#include <vector>

#if !defined(_WIN32)
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

#include <cstdlib>
#include <filesystem>
#include <fstream>
namespace fs = std::filesystem;
#endif

using namespace emmascan;

namespace {

void put16(uint8_t* p, uint16_t v) {
    p[0] = static_cast<uint8_t>(v);
    p[1] = static_cast<uint8_t>(v >> 8);
}
void put32(uint8_t* p, uint32_t v) {
    for (int i = 0; i < 4; ++i) p[i] = static_cast<uint8_t>(v >> (8 * i));
}

// Appends one ext4_dir_entry_2 at `pos`; returns the position after it.
uint32_t add_entry(std::vector<uint8_t>& block, uint32_t pos, uint32_t ino, std::string_view name, uint32_t rec_len) {
    put32(&block[pos], ino);
    put16(&block[pos + 4], static_cast<uint16_t>(rec_len));
    block[pos + 6] = static_cast<uint8_t>(name.size());
    block[pos + 7] = 1;
    std::memcpy(&block[pos + 8], name.data(), name.size());
    return pos + rec_len;
}

std::vector<std::pair<uint32_t, std::string>> entries_of(const std::vector<uint8_t>& block, bool* ok = nullptr) {
    std::vector<std::pair<uint32_t, std::string>> out;
    const bool good = ext4::parse_dir_block(block.data(), static_cast<uint32_t>(block.size()), true,
                                            [&](uint32_t ino, std::string_view name) { out.emplace_back(ino, name); });
    if (ok) *ok = good;
    return out;
}

class MemorySource final : public ext4::BlockSource {
public:
    explicit MemorySource(std::vector<uint8_t> data) : data_(std::move(data)) {}
    bool read(uint64_t offset, void* buffer, size_t length) override {
        if (offset > data_.size() || length > data_.size() - offset) return false;
        std::memcpy(buffer, data_.data() + offset, length);
        return true;
    }
    std::vector<uint8_t>& data() { return data_; }

private:
    std::vector<uint8_t> data_;
};

} // namespace

TEST_CASE("ext4: directory blocks list entries and skip dots and deleted entries and the checksum tail") {
    std::vector<uint8_t> block(1024, 0);
    uint32_t pos = add_entry(block, 0, 2, ".", 12);
    pos = add_entry(block, pos, 2, "..", 12);
    pos = add_entry(block, pos, 12, "hello.txt", 20);
    pos = add_entry(block, pos, 0, "deleted", 16); // inode 0: removed entry
    pos = add_entry(block, pos, 13, "z", 1024 - 12 - pos);
    add_entry(block, pos, 0, "", 12); // metadata_csum tail
    block[pos + 7] = 0xDE;

    bool ok = false;
    const auto entries = entries_of(block, &ok);
    CHECK(ok);
    REQUIRE(entries.size() == 2);
    CHECK(entries[0] == std::pair<uint32_t, std::string>{12, "hello.txt"});
    CHECK(entries[1] == std::pair<uint32_t, std::string>{13, "z"});
}

TEST_CASE("ext4: a damaged directory block stops parsing without reading past the block") {
    std::vector<uint8_t> block(1024, 0);
    const uint32_t pos = add_entry(block, 0, 12, "kept", 16);
    add_entry(block, pos, 13, "broken", 4000); // rec_len beyond the block
    bool ok = true;
    const auto entries = entries_of(block, &ok);
    CHECK_FALSE(ok);
    REQUIRE(entries.size() == 1);
    CHECK(entries[0].second == "kept");

    std::vector<uint8_t> tiny(1024, 0);
    add_entry(tiny, 0, 12, "x", 4); // rec_len smaller than an entry header
    CHECK(entries_of(tiny, &ok).empty());
    CHECK_FALSE(ok);
}

TEST_CASE("ext4: entries whose name is not one path component are dropped and the block reported") {
    std::vector<uint8_t> block(1024, 0);
    uint32_t pos = add_entry(block, 0, 12, "ok", 12);
    pos = add_entry(block, pos, 13, "../victim", 20); // a crafted name must not lead out of the folder
    pos = add_entry(block, pos, 14, std::string_view("nul\0x", 5), 16);
    pos = add_entry(block, pos, 15, "after", 1024 - pos);
    bool ok = true;
    const auto entries = entries_of(block, &ok);
    CHECK_FALSE(ok);
    REQUIRE(entries.size() == 2);
    CHECK(entries[0].second == "ok");
    CHECK(entries[1].second == "after"); // later entries still count

    // Without the filetype feature the name length has 16 bits; more than 255 bytes is invalid.
    std::vector<uint8_t> wide(1024, 0);
    put32(&wide[0], 12);
    put16(&wide[4], 1024);
    put16(&wide[6], 300);
    std::memset(&wide[8], 'a', 300);
    size_t seen = 0;
    CHECK_FALSE(ext4::parse_dir_block(wide.data(), 1024, false, [&](uint32_t, std::string_view) { ++seen; }));
    CHECK(seen == 0);
}

TEST_CASE("utf8: only shortest-form scalar values decode; everything else is U+FFFD per byte") {
    CHECK(utf8_to_utf16("\xC0\xAE\xC0\xAE") == u"����"); // overlong "..": never a dot
    CHECK(utf8_to_utf16("\xC0\xAF") == u"��");                     // overlong "/"
    CHECK(utf8_to_utf16("\xE0\x80\xAF") == u"���");           // overlong "/", 3 bytes
    CHECK(utf8_to_utf16("\xED\xA0\x80") == u"���");           // a surrogate
    CHECK(utf8_to_utf16("\xF4\x90\x80\x80") == u"����"); // past U+10FFFF
    CHECK(utf8_to_utf16("a\xC3") == u"a�");                             // cut short
    CHECK(utf8_to_utf16("\xC3\xBC\xE2\x82\xAC\xF0\x9F\x98\x80") == u"ü€\U0001F600"); // valid: ü € 😀
}

TEST_CASE("ext4: superblock checks") {
    std::vector<uint8_t> sb(1024, 0);
    put32(&sb[0x00], 8192);  // inodes
    put32(&sb[0x04], 65536); // blocks
    put32(&sb[0x14], 0);     // first data block
    put32(&sb[0x18], 2);     // 4 KiB blocks
    put32(&sb[0x20], 32768); // blocks per group
    put32(&sb[0x28], 4096);  // inodes per group
    put16(&sb[0x38], 0xEF53);
    put32(&sb[0x4C], 1);
    put16(&sb[0x58], 256);
    put32(&sb[0x60], 0x2 | 0x40 | 0x200); // filetype, extents, flex_bg

    ext4::Superblock out;
    REQUIRE(ext4::parse_superblock(sb.data(), sb.size(), out) == ext4::SuperblockCheck::Ok);
    CHECK(out.block_size == 4096);
    CHECK(out.group_count == 2);
    CHECK(ext4::descriptor_block(out, 0) == 1);

    auto inline_data = sb;
    put32(&inline_data[0x60], 0x2 | 0x8000);
    CHECK(ext4::parse_superblock(inline_data.data(), inline_data.size(), out) == ext4::SuperblockCheck::Unsupported);

    auto bad_magic = sb;
    put16(&bad_magic[0x38], 0x1234);
    CHECK(ext4::parse_superblock(bad_magic.data(), bad_magic.size(), out) == ext4::SuperblockCheck::NotExt);

    auto bad_geometry = sb;
    put32(&bad_geometry[0x00], 8000); // not inodes_per_group * groups
    CHECK(ext4::parse_superblock(bad_geometry.data(), bad_geometry.size(), out) == ext4::SuperblockCheck::NotExt);

    // Consistent geometry claiming more block groups than the reader sizes arrays for: one inode and eight blocks
    // per group. The largest count allowed parses; one group more (and billions) is left to the walker.
    const auto many_groups = [&](uint64_t groups) {
        auto s = sb;
        put32(&s[0x00], static_cast<uint32_t>(groups)); // inodes = groups * 1
        put32(&s[0x04], static_cast<uint32_t>(groups * 8));
        put32(&s[0x20], 8);
        put32(&s[0x28], 1);
        return s;
    };
    auto at_cap = many_groups(ext4::kMaxGroups);
    REQUIRE(ext4::parse_superblock(at_cap.data(), at_cap.size(), out) == ext4::SuperblockCheck::Ok);
    CHECK(out.group_count == ext4::kMaxGroups);
    auto past_cap = many_groups(uint64_t{ext4::kMaxGroups} + 1);
    CHECK(ext4::parse_superblock(past_cap.data(), past_cap.size(), out) == ext4::SuperblockCheck::Unsupported);
    auto billions = many_groups(uint64_t{UINT32_MAX} / 8); // the block count must fit in 32 bits here
    CHECK(ext4::parse_superblock(billions.data(), billions.size(), out) == ext4::SuperblockCheck::Unsupported);
}

TEST_CASE("ext4: inode times use the extra epoch bits and nanoseconds") {
    ext4::Superblock sb;
    sb.block_size = 4096;
    sb.inode_size = 256;
    std::vector<uint8_t> inode(256, 0);
    put16(&inode[0x00], 0x81A4); // regular file 0644
    put16(&inode[0x1A], 1);
    put32(&inode[0x04], 1234);
    put32(&inode[0x1C], 8); // 8 sectors
    put32(&inode[0x10], 0); // mtime seconds
    put16(&inode[0x80], 32);
    put32(&inode[0x88], (1u << 0) | (500000000u << 2)); // +2^32 s, 0.5 s
    put32(&inode[0x08], static_cast<uint32_t>(-86400)); // atime: one day before 1970, no extra

    ext4::Inode out;
    REQUIRE(ext4::parse_inode(inode.data(), sb, out));
    CHECK(out.size == 1234);
    CHECK(out.allocated == 8 * 512);
    CHECK(out.mtime == (int64_t{1} << 32) * 1000 + 500);
    CHECK(out.atime == -86400LL * 1000);

    put16(&inode[0x1A], 0); // no links: deleted
    CHECK_FALSE(ext4::parse_inode(inode.data(), sb, out));
}

TEST_CASE("ext4: a buffer that is not a file system is not supported") {
    MemorySource src(std::vector<uint8_t>(64 * 1024, 0));
    ext4::Ext4Index index;
    CHECK(index.load(src) == ES_ERR_NOT_SUPPORTED);
    MemorySource tiny(std::vector<uint8_t>(100, 0));
    CHECK(index.load(tiny) == ES_ERR_IO);
}

#if !defined(_WIN32)

namespace {

class FileSource final : public ext4::BlockSource {
public:
    explicit FileSource(const fs::path& path) : fd_(open(path.c_str(), O_RDONLY | O_CLOEXEC)) {}
    ~FileSource() override {
        if (fd_ >= 0) close(fd_);
    }
    bool read(uint64_t offset, void* buffer, size_t length) override {
        return pread(fd_, buffer, length, static_cast<off_t>(offset)) == static_cast<ssize_t>(length);
    }

private:
    int fd_;
};

bool have_mke2fs() { return std::system("command -v mke2fs >/dev/null 2>&1 && command -v e2fsck >/dev/null 2>&1") == 0; }

std::string quote(const fs::path& p) {
    std::string s = "'";
    for (char c : p.string()) s += c == '\'' ? std::string("'\\''") : std::string(1, c);
    return s + "'";
}

void write_file(const fs::path& p, size_t bytes, char fill = 'x') {
    std::ofstream(p, std::ios::binary) << std::string(bytes, fill);
}

struct Fixture {
    fs::path base;
    fs::path src;

    Fixture() {
        std::random_device rd;
        base = fs::temp_directory_path() / ("emmascan-ext4-" + std::to_string(rd()));
        src = base / "src";
        fs::create_directories(src / "docs" / "deep" / "deeper");
        fs::create_directories(src / "empty");
        fs::create_directories(src / "links");
        fs::create_directories(src / "many");
        write_file(src / "zero.bin", 0);
        write_file(src / "big.bin", 1 << 20);
        write_file(src / "docs" / "a.txt", 100);
        write_file(src / "docs" / "deep" / "b.txt", 200);
        write_file(src / "docs" / "deep" / "deeper" / u8"ünïcödé.dat", 300);
        write_file(src / "with space and\nnewline", 11);
        write_file(src / std::string("odd-\xff\xfe-bytes"), 7);
        // Sparse: 5 GiB logical (needs the high size word), one block of data at the end.
        {
            const int fd = open((src / "sparse.bin").c_str(), O_WRONLY | O_CREAT | O_TRUNC, 0644);
            REQUIRE(fd >= 0);
            REQUIRE(pwrite(fd, "end", 3, (5ll << 30) - 3) == 3);
            close(fd);
        }
        write_file(src / "links" / "one.bin", 7000);
        fs::create_hard_link(src / "links" / "one.bin", src / "links" / "two.bin");
        fs::create_hard_link(src / "docs" / "a.txt", src / "a_link.txt"); // other name outside docs/
        fs::create_symlink("docs", src / "dir_link");
        fs::create_symlink("big.bin", src / "file_link");
        fs::create_symlink(std::string(100, 'L'), src / "long_link"); // target stored in a block
        REQUIRE(mkfifo((src / "fifo").c_str(), 0644) == 0);
        for (int i = 0; i < 5000; ++i) write_file(src / "many" / ("file-" + std::to_string(100000 + i)), 1);
        fs::path deep = src / "chain";
        for (int i = 0; i < 30; ++i) deep /= "level" + std::to_string(i);
        fs::create_directories(deep);
        write_file(deep / "bottom.txt", 42);
    }
    ~Fixture() {
        std::error_code ec;
        fs::remove_all(base, ec);
    }

    // Returns the image path, or an empty path if mke2fs refused these options.
    fs::path make_image(const std::string& name, const std::string& mke2fs_options, const char* size = "256M") const {
        const fs::path image = base / (name + ".img");
        const std::string make = "mke2fs -q -F " + mke2fs_options + " -d " + quote(src) + " " + quote(image) +
                                 " " + size + " >/dev/null 2>&1";
        if (std::system(make.c_str()) != 0) return {};
        // -D rebuilds directories, which indexes big ones (htree). Exit code 1 = "fixed something", fine.
        const int fsck = std::system(("e2fsck -fyD " + quote(image) + " >/dev/null 2>&1").c_str());
        if (!WIFEXITED(fsck) || WEXITSTATUS(fsck) > 1) return {};
        return image;
    }
};

struct Item {
    uint16_t flags;
    uint64_t size;
    uint32_t files;
    uint32_t dirs;
};

// path (UTF-8, relative to the root) -> what was recorded
std::map<std::string, Item> flatten(const es_node* nodes, uint32_t count, const char16_t* names) {
    std::map<std::string, Item> out;
    std::vector<std::string> paths(count);
    for (uint32_t i = 1; i < count; ++i) {
        const es_node& n = nodes[i];
        const std::string name = utf16_to_utf8({names + n.name_offset, n.name_length});
        paths[i] = n.parent == 0 ? name : paths[n.parent] + "/" + name;
        out[paths[i]] = {n.flags, n.size, n.file_count, n.dir_count};
    }
    return out;
}

struct WalkResult {
    std::map<std::string, Item> items;
    es_node root;
};

WalkResult walk(const fs::path& dir) {
    const std::u16string path = utf8_to_utf16(dir.string());
    es_scan_options options{};
    options.struct_size = sizeof(options);
    options.mode = ES_MODE_WALKER;
    options.root_path = reinterpret_cast<const es_char*>(path.c_str());
    es_scan* scan = nullptr;
    REQUIRE(es_scan_start(&options, &scan) == ES_OK);
    REQUIRE(es_scan_wait(scan, 120000) == ES_OK);
    es_result r{};
    REQUIRE(es_scan_get_result(scan, &r) == ES_OK);
    WalkResult out{flatten(r.nodes, r.node_count, reinterpret_cast<const char16_t*>(r.names)), r.nodes[0]};
    es_scan_free(scan);
    return out;
}

WalkResult from_index(const ext4::Ext4Index& index, uint32_t root_ino,
                      const std::vector<uint32_t>& skip = {}) {
    NodeStore store;
    std::atomic<bool> cancel{false};
    uint64_t files = 0, dirs = 0, bytes = 0;
    uint32_t errors = 0;
    REQUIRE(index.build_tree(root_ino, u"img", skip, store, cancel, files, dirs, bytes, errors));
    CHECK(errors == 0);
    store.finalize();
    WalkResult out{flatten(store.nodes().data(), static_cast<uint32_t>(store.nodes().size()), store.names().data()),
                   store.nodes()[0]};
    // mke2fs adds lost+found; the source folder has none.
    if (root_ino == ext4::kRootIno && out.items.erase("lost+found")) --out.root.dir_count;
    return out;
}

// Same names and kinds; same sizes and counts, except which name of a hard-linked file carries its size
// (that depends on the order things are listed in).
void compare(const WalkResult& expected, const WalkResult& actual) {
    constexpr uint16_t kLinkFlags = ES_NODE_HARDLINK | ES_NODE_HAS_LINKS;
    CHECK(actual.items.size() == expected.items.size());
    for (const auto& [path, want] : expected.items) {
        INFO(path);
        const auto it = actual.items.find(path);
        REQUIRE(it != actual.items.end());
        const Item& got = it->second;
        CHECK((got.flags & ~kLinkFlags) == (want.flags & ~kLinkFlags));
        CHECK(got.files == want.files);
        CHECK(got.dirs == want.dirs);
        if (!((got.flags | want.flags) & kLinkFlags) && !(want.flags & ES_NODE_DIRECTORY)) CHECK(got.size == want.size);
    }
    CHECK(actual.root.size == expected.root.size);
    CHECK(actual.root.file_count == expected.root.file_count);
    CHECK(actual.root.dir_count == expected.root.dir_count);
}

} // namespace

TEST_CASE("ext4: images of every layout match the walker") {
    if (!have_mke2fs()) SKIP("mke2fs/e2fsck not installed");
    Fixture fx;
    const WalkResult expected = walk(fx.src);
    REQUIRE(expected.items.size() > 5000);

    struct Layout {
        const char* name;
        const char* options;
        const char* size;
    };
    const Layout layouts[] = {
        {"ext4", "-t ext4 -b 4096", "256M"},
        {"ext4-mke2fs-default", "-t ext4", "256M"}, // small images get 1 KiB blocks from mke2fs.conf
        {"ext4-no-csum-32bit", "-t ext4 -O ^metadata_csum,^64bit", "256M"},
        {"ext4-1k-128", "-t ext4 -b 1024 -I 128", "256M"},
        {"ext4-meta-bg", "-t ext4 -O meta_bg,^resize_inode", "256M"},
        {"ext4-64k", "-t ext4 -b 65536", "1G"}, // every small file takes a whole 64 KiB block
        {"ext3", "-t ext3", "256M"},
        {"ext2", "-t ext2", "256M"},
        {"ext2-1k", "-t ext2 -b 1024", "256M"},
    };
    for (const auto& [name, options, size] : layouts) {
        DYNAMIC_SECTION(name) {
            const fs::path image = fx.make_image(name, options, size);
            if (image.empty()) SKIP("mke2fs cannot make " << name);
            FileSource src(image);
            ext4::Ext4Index index;
            REQUIRE(index.load(src) == ES_OK);
            compare(expected, from_index(index, ext4::kRootIno));
        }
    }
}

TEST_CASE("ext4: subfolders and hard links reaching outside them and mount points") {
    if (!have_mke2fs()) SKIP("mke2fs/e2fsck not installed");
    Fixture fx;
    const fs::path image = fx.make_image("sub", "-t ext4");
    REQUIRE_FALSE(image.empty());
    FileSource src(image);
    ext4::Ext4Index index;
    REQUIRE(index.load(src) == ES_OK);

    const uint32_t docs = index.find_child(ext4::kRootIno, "docs");
    REQUIRE(docs != 0);
    const WalkResult sub = from_index(index, docs);
    compare(walk(fx.src / "docs"), sub);
    CHECK(sub.items.at("a.txt").size == 100); // its other name (a_link.txt) is outside docs/
    CHECK(sub.items.at("a.txt").flags == ES_NODE_HAS_LINKS);

    const WalkResult skipped = from_index(index, ext4::kRootIno, {docs});
    CHECK(skipped.items.at("docs").flags == (ES_NODE_DIRECTORY | ES_NODE_REPARSE));
    CHECK_FALSE(skipped.items.contains("docs/a.txt"));
    CHECK(skipped.items.contains("many/file-100000"));

    const auto& links = skipped.items;
    const Item one = links.at("links/one.bin"), two = links.at("links/two.bin");
    CHECK(one.size + two.size == 7000); // counted once
    CHECK(((one.flags | two.flags) & ES_NODE_HARDLINK) != 0);
}

namespace {

bool have_debugfs() { return std::system("command -v debugfs >/dev/null 2>&1") == 0; }

// A linear (unindexed) image, so every block of many/ holds plain entries; no e2fsck -D afterwards.
fs::path make_linear_image(const Fixture& fx, const std::string& name) {
    const fs::path image = fx.base / (name + ".img");
    const std::string make = "mke2fs -q -F -t ext4 -b 4096 -O ^dir_index -d " + quote(fx.src) + " " + quote(image) +
                             " 256M >/dev/null 2>&1";
    return std::system(make.c_str()) == 0 ? image : fs::path{};
}

struct Built {
    std::map<std::string, Item> items;
    uint32_t errors = 0;
};

Built build(const ext4::Ext4Index& index) {
    NodeStore store;
    std::atomic<bool> cancel{false};
    uint64_t files = 0, dirs = 0, bytes = 0;
    Built out;
    REQUIRE(index.build_tree(ext4::kRootIno, u"img", {}, store, cancel, files, dirs, bytes, out.errors));
    store.finalize();
    out.items = flatten(store.nodes().data(), static_cast<uint32_t>(store.nodes().size()), store.names().data());
    return out;
}

size_t children_of(const Built& b, const std::string& dir) {
    size_t n = 0;
    for (const auto& [path, item] : b.items)
        if (path.starts_with(dir + "/") && path.find('/', dir.size() + 1) == std::string::npos) ++n;
    return n;
}

} // namespace

TEST_CASE("ext4: a folder with a hole keeps the entries after the hole") {
    if (!have_mke2fs() || !have_debugfs()) SKIP("mke2fs/debugfs not installed");
    Fixture fx;
    const fs::path image = make_linear_image(fx, "hole");
    REQUIRE_FALSE(image.empty());
    // Free blocks 1 and 2 of many/ (5000 entries, ~25 blocks): its size stays, two blocks fewer are allocated, so a
    // limit taken from the allocation alone would also cut off the last two blocks (one may be an empty spare).
    // First count the entries in the freed blocks, which are the only ones that may go missing.
    size_t in_block = 0;
    for (int logical = 1; logical <= 2; ++logical) {
        const std::string cmd = "debugfs -R 'bmap /many " + std::to_string(logical) + "' " + quote(image) + " 2>/dev/null";
        FILE* p = popen(cmd.c_str(), "r");
        REQUIRE(p != nullptr);
        unsigned long long physical = 0;
        const bool got = std::fscanf(p, "%llu", &physical) == 1;
        pclose(p);
        REQUIRE(got);
        FileSource raw(image);
        std::vector<uint8_t> block(4096);
        REQUIRE(raw.read(physical * 4096, block.data(), block.size()));
        REQUIRE(ext4::parse_dir_block(block.data(), 4096, true, [&](uint32_t, std::string_view) { ++in_block; }));
    }
    REQUIRE(in_block > 0);
    REQUIRE(std::system(("debugfs -w -R 'punch /many 1 2' " + quote(image) + " >/dev/null 2>&1").c_str()) == 0);
    FileSource src(image);
    ext4::Ext4Index index;
    REQUIRE(index.load(src) == ES_OK);
    const Built b = build(index);
    CHECK(children_of(b, "many") == 5000 - in_block); // everything after the hole is still listed
    CHECK(b.items.at("many").flags == ES_NODE_DIRECTORY);
    CHECK(b.errors == 0);
}

TEST_CASE("ext4: a damaged folder block flags the folder but keeps its other entries") {
    if (!have_mke2fs() || !have_debugfs()) SKIP("mke2fs/debugfs not installed");
    Fixture fx;
    const fs::path image = make_linear_image(fx, "damaged");
    REQUIRE_FALSE(image.empty());
    // Physical blocks of many/, from debugfs ("blocks" prints them space-separated).
    std::vector<uint64_t> blocks;
    {
        const std::string cmd = "debugfs -R 'blocks /many' " + quote(image) + " 2>/dev/null";
        FILE* p = popen(cmd.c_str(), "r");
        REQUIRE(p != nullptr);
        unsigned long long b = 0;
        while (std::fscanf(p, "%llu", &b) == 1) blocks.push_back(b);
        pclose(p);
    }
    REQUIRE(blocks.size() > 5);
    {
        // The first entry of the fourth block gets rec_len 3, which no valid entry has.
        const int fd = open(image.c_str(), O_RDWR | O_CLOEXEC);
        REQUIRE(fd >= 0);
        const uint8_t bad[2] = {3, 0};
        REQUIRE(pwrite(fd, bad, 2, static_cast<off_t>(blocks[3] * 4096 + 4)) == 2);
        close(fd);
    }
    FileSource src(image);
    ext4::Ext4Index index;
    REQUIRE(index.load(src) == ES_OK);
    const Built b = build(index);
    CHECK(b.items.at("many").flags == (ES_NODE_DIRECTORY | ES_NODE_ERROR));
    CHECK(b.errors == 1);
    const size_t listed = children_of(b, "many");
    CHECK(listed < 5000);
    CHECK(listed > 5000 - 400);
    CHECK(b.items.contains("docs/deep/deeper/" + utf16_to_utf8(u"ünïcödé.dat"))); // the rest of the tree is intact
}

TEST_CASE("ext4: inline_data is left to the walker") {
    if (!have_mke2fs()) SKIP("mke2fs/e2fsck not installed");
    Fixture fx;
    const fs::path image = fx.make_image("inline", "-t ext4 -O inline_data");
    if (image.empty()) SKIP("mke2fs cannot make inline_data");
    FileSource src(image);
    ext4::Ext4Index index;
    CHECK(index.load(src) == ES_ERR_NOT_SUPPORTED);
}

TEST_CASE("ext4: damaged metadata never crashes or hangs the reader") {
    if (!have_mke2fs()) SKIP("mke2fs/e2fsck not installed");
    Fixture fx;
    const fs::path image = fx.make_image("fuzz", "-t ext4 -b 1024", "24M");
    REQUIRE_FALSE(image.empty());
    std::vector<uint8_t> original;
    {
        std::ifstream in(image, std::ios::binary);
        original.assign(std::istreambuf_iterator<char>(in), {});
    }
    // Metadata (descriptors, inode tables, directory blocks) sits near the start of a fresh image.
    const size_t region = std::min<size_t>(original.size(), 16 << 20);
    MemorySource src(original);
    for (uint32_t seed = 1; seed <= 150; ++seed) {
        INFO("seed " << seed);
        std::mt19937 rng(seed);
        src.data() = original;
        // Every fifth run damages only the superblock (bytes 1024..2047), the others any metadata including it.
        const size_t from = 1024, span = seed % 5 == 0 ? 1024 : region - from;
        const int flips = seed % 5 == 0 ? 8 : 200;
        for (int i = 0; i < flips; ++i) src.data()[from + rng() % span] ^= static_cast<uint8_t>(1 + rng() % 255);
        ext4::Ext4Index index;
        ext4::ReadOptions options;
        options.threads = 2;
        if (index.load(src, options) != ES_OK) continue;
        NodeStore store;
        std::atomic<bool> cancel{false};
        uint64_t files = 0, dirs = 0, bytes = 0;
        uint32_t errors = 0;
        if (index.build_tree(ext4::kRootIno, u"img", {}, store, cancel, files, dirs, bytes, errors)) {
            CHECK(store.nodes().size() <= index.entry_count() + 1);
            store.finalize();
        }
    }
}

TEST_CASE("ext4: explicit ext4 mode on another file system is refused cleanly") {
#if defined(__APPLE__)
    const std::u16string path = u"/"; // there is no ext4 reader on macOS at all
#else
    const std::u16string path = u"/proc"; // procfs
#endif
    es_scan_options options{};
    options.struct_size = sizeof(options);
    options.mode = ES_MODE_EXT4;
    options.root_path = reinterpret_cast<const es_char*>(path.c_str());
    es_scan* scan = nullptr;
    REQUIRE(es_scan_start(&options, &scan) == ES_OK);
    const es_status status = es_scan_wait(scan, 60000);
    CHECK(status == ES_ERR_NOT_SUPPORTED);
    es_scan_free(scan);
}

#if !defined(__APPLE__)
TEST_CASE("ext4: as root a loop-mounted image scanned in ext4 mode equals the walker exactly") {
    if (geteuid() != 0) SKIP("needs root (loop mount and the block device)");
    if (!have_mke2fs()) SKIP("mke2fs/e2fsck not installed");
    Fixture fx;
    const fs::path image = fx.make_image("loop", "-t ext4");
    REQUIRE_FALSE(image.empty());
    const fs::path mnt = fx.base / "mnt";
    fs::create_directories(mnt);
    if (std::system(("mount -o loop " + quote(image) + " " + quote(mnt) + " >/dev/null 2>&1").c_str()) != 0)
        SKIP("cannot loop-mount");
    struct Unmount {
        fs::path p;
        ~Unmount() { std::system(("umount " + quote(p) + " >/dev/null 2>&1").c_str()); }
    } unmount{mnt};
    // Where / is a shared mount (systemd's default, e.g. on CI runners) the loop mount is shared too, and so is
    // the bind mount made from it below: the tmpfs on empty/deep would then also be mounted on docs/deep. Keep
    // the mounts made here from propagating to each other, as the bind mount check below expects.
    REQUIRE(std::system(("mount --make-private " + quote(mnt) + " >/dev/null 2>&1").c_str()) == 0);
    // A change not yet written back must still be seen (syncfs + buffered device reads).
    write_file(mnt / "fresh.txt", 12345);

    const auto scan_with = [&](uint32_t mode, uint32_t& mode_used) {
        const std::u16string path = utf8_to_utf16(mnt.string());
        es_scan_options options{};
        options.struct_size = sizeof(options);
        options.mode = mode;
        options.root_path = reinterpret_cast<const es_char*>(path.c_str());
        es_scan* scan = nullptr;
        REQUIRE(es_scan_start(&options, &scan) == ES_OK);
        REQUIRE(es_scan_wait(scan, 120000) == ES_OK);
        es_result r{};
        REQUIRE(es_scan_get_result(scan, &r) == ES_OK);
        mode_used = r.mode;
        // Exact: allocated sizes and times too.
        std::map<std::string, std::tuple<uint16_t, uint64_t, uint64_t, int64_t, int64_t, uint32_t>> items;
        std::vector<std::string> paths(r.node_count);
        const auto* names = reinterpret_cast<const char16_t*>(r.names);
        for (uint32_t i = 1; i < r.node_count; ++i) {
            const es_node& n = r.nodes[i];
            paths[i] = (n.parent == 0 ? "" : paths[n.parent] + "/") + utf16_to_utf8({names + n.name_offset, n.name_length});
            const bool linked = n.flags & (ES_NODE_HARDLINK | ES_NODE_HAS_LINKS);
            items[paths[i]] = {static_cast<uint16_t>(n.flags & ~(ES_NODE_HARDLINK | ES_NODE_HAS_LINKS)),
                               linked ? 0 : n.size, linked ? 0 : n.allocated, n.mtime, n.atime, n.attributes};
        }
        const es_node root = r.nodes[0];
        es_scan_free(scan);
        return std::make_pair(items, std::make_tuple(root.size, root.allocated, root.file_count, root.dir_count));
    };
    uint32_t walker_mode = 0, ext4_mode = 0;
    // ext4 first: the walker stats each folder before listing it, and listing a folder can update its access
    // time, so the walker never sees its own side effect but a later ext4 scan would.
    const auto ext4 = scan_with(ES_MODE_EXT4, ext4_mode);
    const auto walker = scan_with(ES_MODE_WALKER, walker_mode);
    CHECK(walker_mode == ES_MODE_WALKER);
    CHECK(ext4_mode == ES_MODE_EXT4);
    CHECK(ext4.second == walker.second);
    CHECK(ext4.first.size() == walker.first.size());
    for (const auto& [path, want] : walker.first) {
        INFO(path);
        const auto it = ext4.first.find(path);
        REQUIRE(it != ext4.first.end());
        const auto& got = it->second;
        CHECK(std::get<0>(got) == std::get<0>(want)); // flags
        CHECK(std::get<1>(got) == std::get<1>(want)); // size
        CHECK(std::get<2>(got) == std::get<2>(want)); // allocated
        CHECK(std::get<3>(got) == std::get<3>(want)); // mtime
        CHECK(std::get<4>(got) == std::get<4>(want)); // atime
        CHECK(std::get<5>(got) == std::get<5>(want)); // mode
    }
    CHECK(ext4.first.contains("fresh.txt"));

    uint32_t auto_mode = 0;
    scan_with(ES_MODE_AUTO, auto_mode);
    CHECK(auto_mode == ES_MODE_EXT4); // a mount root, as root

    // Mounts inside the scanned file system: docs/ bound onto empty/, and a tmpfs on empty/deep, which through the
    // bind mount is the same folder as docs/deep. Only empty/ is a mount point here; docs/deep is an ordinary
    // folder on the image and must keep its content (looking mount points up with stat() got this wrong).
    if (std::system(("mount --bind " + quote(mnt / "docs") + " " + quote(mnt / "empty") + " >/dev/null 2>&1").c_str()) != 0)
        return;
    Unmount unbind{mnt / "empty"};
    if (std::system(("mount -t tmpfs none " + quote(mnt / "empty" / "deep") + " >/dev/null 2>&1").c_str()) != 0) return;
    Unmount untmpfs{mnt / "empty" / "deep"};
    uint32_t mounted_mode = 0;
    const auto mounted = scan_with(ES_MODE_EXT4, mounted_mode);
    REQUIRE(mounted.first.contains("empty"));
    CHECK(std::get<0>(mounted.first.at("empty")) == (ES_NODE_DIRECTORY | ES_NODE_REPARSE));
    CHECK_FALSE(mounted.first.contains("empty/a.txt"));
    REQUIRE(mounted.first.contains("docs/deep"));
    CHECK(std::get<0>(mounted.first.at("docs/deep")) == ES_NODE_DIRECTORY);
    CHECK(mounted.first.contains("docs/deep/b.txt"));
}
#endif

#endif // !_WIN32
