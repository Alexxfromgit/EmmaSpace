#include "ntfs/mft_parser.h"
#include "time_util.h"

#include <catch2/catch_test_macros.hpp>

#include <cstring>
#include <string>
#include <vector>

using namespace emmascan;
using namespace emmascan::ntfs;

namespace {

constexpr int64_t kFiletime2024 = 133485408000000000LL; // 2024-01-01T00:00:00Z
constexpr int64_t kUnixMs2024 = 1704067200000LL;

template <typename T>
void put(std::vector<uint8_t>& b, size_t at, T v) {
    std::memcpy(b.data() + at, &v, sizeof(T));
}

size_t align8(size_t v) { return (v + 7) & ~size_t{7}; }

// Writes FILE records the way NTFS stores them, update sequence array included.
class RecordBuilder {
public:
    explicit RecordBuilder(uint16_t flags, uint16_t sequence = 1, uint64_t base_ref = 0, size_t size = 1024)
        : buf_(size, 0), usa_count_(static_cast<uint16_t>(size / 512 + 1)) {
        std::memcpy(buf_.data(), "FILE", 4);
        put<uint16_t>(buf_, 4, 0x30);
        put<uint16_t>(buf_, 6, usa_count_);
        put<uint16_t>(buf_, 0x10, sequence);
        pos_ = align8(0x30 + size_t{usa_count_} * 2);
        put<uint16_t>(buf_, 0x14, static_cast<uint16_t>(pos_));
        put<uint16_t>(buf_, 0x16, flags);
        put<uint32_t>(buf_, 0x1C, static_cast<uint32_t>(size));
        put<uint64_t>(buf_, 0x20, base_ref);
    }

    RecordBuilder& resident(uint32_t type, const std::vector<uint8_t>& value, std::u16string_view name = {}) {
        const size_t value_offset = align8(0x18 + name.size() * 2);
        const size_t length = align8(value_offset + value.size());
        put<uint32_t>(buf_, pos_, type);
        put<uint32_t>(buf_, pos_ + 4, static_cast<uint32_t>(length));
        buf_[pos_ + 9] = static_cast<uint8_t>(name.size());
        put<uint16_t>(buf_, pos_ + 0x0A, 0x18);
        put<uint32_t>(buf_, pos_ + 0x10, static_cast<uint32_t>(value.size()));
        put<uint16_t>(buf_, pos_ + 0x14, static_cast<uint16_t>(value_offset));
        std::memcpy(buf_.data() + pos_ + 0x18, name.data(), name.size() * 2);
        std::memcpy(buf_.data() + pos_ + value_offset, value.data(), value.size());
        pos_ += length;
        return *this;
    }

    RecordBuilder& non_resident_data(uint64_t allocated, uint64_t size, const std::vector<uint8_t>& runlist,
                                     uint16_t attr_flags = 0, uint64_t compressed = 0) {
        const size_t header = (attr_flags & 0x8001) ? 0x48 : 0x40;
        const size_t length = align8(header + runlist.size() + 1);
        put<uint32_t>(buf_, pos_, kAttrData);
        put<uint32_t>(buf_, pos_ + 4, static_cast<uint32_t>(length));
        buf_[pos_ + 8] = 1;
        put<uint16_t>(buf_, pos_ + 0x0A, static_cast<uint16_t>(header));
        put<uint16_t>(buf_, pos_ + 0x0C, attr_flags);
        put<uint16_t>(buf_, pos_ + 0x20, static_cast<uint16_t>(header));
        put<uint64_t>(buf_, pos_ + 0x28, allocated);
        put<uint64_t>(buf_, pos_ + 0x30, size);
        put<uint64_t>(buf_, pos_ + 0x38, size);
        if (header == 0x48) put<uint64_t>(buf_, pos_ + 0x40, compressed);
        std::memcpy(buf_.data() + pos_ + header, runlist.data(), runlist.size());
        pos_ += length;
        return *this;
    }

    std::vector<uint8_t> finish() {
        put<uint32_t>(buf_, pos_, kAttrEnd);
        put<uint32_t>(buf_, 0x18, static_cast<uint32_t>(pos_ + 8));
        constexpr uint16_t usn = 0x0042;
        put<uint16_t>(buf_, 0x30, usn);
        for (size_t i = 1; i < usa_count_; ++i) {
            std::memcpy(buf_.data() + 0x30 + i * 2, buf_.data() + i * 512 - 2, 2);
            put<uint16_t>(buf_, i * 512 - 2, usn);
        }
        return buf_;
    }

private:
    std::vector<uint8_t> buf_;
    uint16_t usa_count_;
    size_t pos_;
};

std::vector<uint8_t> standard_info(uint32_t attributes = 0x20) {
    std::vector<uint8_t> v(48, 0);
    put<int64_t>(v, 8, kFiletime2024);
    put<int64_t>(v, 24, kFiletime2024);
    put<uint32_t>(v, 32, attributes);
    return v;
}

std::vector<uint8_t> file_name(uint64_t parent, uint16_t parent_sequence, std::u16string_view name, uint8_t name_space = 1) {
    std::vector<uint8_t> v(0x42 + name.size() * 2, 0);
    put<uint64_t>(v, 0, parent | (uint64_t{parent_sequence} << 48));
    v[0x40] = static_cast<uint8_t>(name.size());
    v[0x41] = name_space;
    std::memcpy(v.data() + 0x42, name.data(), name.size() * 2);
    return v;
}

std::vector<uint8_t> bytes_of_size(size_t n) { return std::vector<uint8_t>(n, 0xAB); }

std::vector<uint8_t> reparse(uint32_t tag) {
    std::vector<uint8_t> v(8, 0);
    put<uint32_t>(v, 0, tag);
    return v;
}

void add(MftIndex& index, uint32_t frn, std::vector<uint8_t> record) {
    REQUIRE(apply_fixups(record.data(), record.size()));
    ParsedRecord parsed;
    REQUIRE(parse_record(record.data(), record.size(), parsed));
    index.add(frn, parsed);
}

constexpr uint16_t kInUse = 1, kDir = 3;

std::u16string_view name_of(const NodeStore& s, uint32_t node) { return s.name_of(node); }

uint32_t child_named(const NodeStore& s, uint32_t parent, std::u16string_view name) {
    for (uint32_t c = s.nodes()[parent].first_child; c != ES_NONE; c = s.nodes()[c].next_sibling)
        if (name_of(s, c) == name) return c;
    return ES_NONE;
}

} // namespace

TEST_CASE("fixups restore sector tails and detect torn records") {
    auto record = RecordBuilder(kInUse).resident(kAttrStandardInformation, standard_info()).finish();
    auto torn = record;
    REQUIRE(apply_fixups(record.data(), record.size()));
    CHECK(record[510] == 0);
    CHECK(record[1022] == 0);

    torn[1022] ^= 0xFF; // second sector written by a different update
    CHECK_FALSE(apply_fixups(torn.data(), torn.size()));
}

TEST_CASE("runlists decode relative and sparse runs") {
    // 16 clusters at LCN 256; 8 clusters at LCN 256-16; 4 sparse clusters.
    const std::vector<uint8_t> runs = {0x21, 0x10, 0x00, 0x01, 0x11, 0x08, 0xF0, 0x01, 0x04, 0x00};
    std::vector<DataRun> out;
    REQUIRE(decode_runlist(runs.data(), runs.data() + runs.size(), out));
    REQUIRE(out.size() == 3);
    CHECK(out[0].lcn == 256);
    CHECK(out[0].length == 16);
    CHECK(out[1].lcn == 240);
    CHECK(out[1].length == 8);
    CHECK(out[2].lcn == -1);
    CHECK(out[2].length == 4);

    const std::vector<uint8_t> truncated = {0x21, 0x10};
    CHECK_FALSE(decode_runlist(truncated.data(), truncated.data() + truncated.size(), out));
}

TEST_CASE("a FILE record yields times, names and sizes") {
    auto record = RecordBuilder(kInUse, 7)
                      .resident(kAttrStandardInformation, standard_info(0x21))
                      .resident(kAttrFileName, file_name(kRootFrn, 5, u"LONGNA~1.TXT", kNamespaceDos))
                      .resident(kAttrFileName, file_name(kRootFrn, 5, u"long name.txt"))
                      .resident(kAttrData, bytes_of_size(300), u"Zone.Identifier") // ADS: ignored
                      .non_resident_data(8192, 5000, {0x11, 0x02, 0x10, 0x00})
                      .finish();
    REQUIRE(apply_fixups(record.data(), record.size()));
    ParsedRecord r;
    REQUIRE(parse_record(record.data(), record.size(), r, true));
    CHECK(r.in_use);
    CHECK_FALSE(r.is_directory);
    CHECK(r.sequence == 7);
    CHECK(r.mtime == kUnixMs2024);
    CHECK(r.attributes == 0x21);
    REQUIRE(r.names.size() == 1); // DOS name skipped
    CHECK(r.names[0].name == u"long name.txt");
    CHECK((r.names[0].parent_ref & kFrnMask) == kRootFrn);
    CHECK(r.has_data);
    CHECK(r.data_size == 5000);
    CHECK(r.allocated == 8192);
    REQUIRE(r.data_runs.size() == 1);
    CHECK(r.data_runs[0].lcn == 16);
}

TEST_CASE("compressed files report their compressed allocation") {
    auto record = RecordBuilder(kInUse).non_resident_data(65536, 60000, {0x11, 0x10, 0x20, 0x00}, 0x0001, 12288).finish();
    REQUIRE(apply_fixups(record.data(), record.size()));
    ParsedRecord r;
    REQUIRE(parse_record(record.data(), record.size(), r));
    CHECK(r.data_size == 60000);
    CHECK(r.allocated == 12288);
}

TEST_CASE("garbage is not a FILE record") {
    std::vector<uint8_t> junk(1024, 0x5A);
    ParsedRecord r;
    CHECK_FALSE(parse_record(junk.data(), junk.size(), r));
}

TEST_CASE("the index builds a tree with links, junctions and extension records") {
    MftIndex index(64);
    add(index, kRootFrn, RecordBuilder(kDir, 5).resident(kAttrStandardInformation, standard_info(0x16))
                             .resident(kAttrFileName, file_name(kRootFrn, 5, u".")).finish());
    add(index, 30, RecordBuilder(kDir, 2).resident(kAttrStandardInformation, standard_info(0x10))
                       .resident(kAttrFileName, file_name(kRootFrn, 5, u"docs")).finish());
    add(index, 31, RecordBuilder(kInUse).resident(kAttrStandardInformation, standard_info())
                       .resident(kAttrFileName, file_name(30, 2, u"a.txt"))
                       .non_resident_data(4096, 1000, {0x11, 0x01, 0x40, 0x00}).finish());
    // big.bin with a second hard link inside docs
    add(index, 32, RecordBuilder(kInUse).resident(kAttrStandardInformation, standard_info())
                       .resident(kAttrFileName, file_name(kRootFrn, 5, u"big.bin"))
                       .resident(kAttrFileName, file_name(30, 2, u"link.bin"))
                       .non_resident_data(8192, 5000, {0x11, 0x02, 0x50, 0x00}).finish());
    // Deleted file: not in use
    add(index, 33, RecordBuilder(0).resident(kAttrFileName, file_name(kRootFrn, 5, u"deleted.tmp"))
                       .resident(kAttrData, bytes_of_size(100)).finish());
    // Stale reference: parent sequence no longer matches docs
    add(index, 34, RecordBuilder(kInUse).resident(kAttrFileName, file_name(30, 1, u"orphan.txt"))
                       .resident(kAttrData, bytes_of_size(77)).finish());
    // Junction: a link, not descended
    add(index, 36, RecordBuilder(kDir).resident(kAttrStandardInformation, standard_info(0x410))
                       .resident(kAttrFileName, file_name(kRootFrn, 5, u"Junction"))
                       .resident(kAttrReparsePoint, reparse(0xA0000003)).finish());
    // Cloud placeholder folder: a reparse point that holds real files
    add(index, 37, RecordBuilder(kDir).resident(kAttrStandardInformation, standard_info(0x410))
                       .resident(kAttrFileName, file_name(kRootFrn, 5, u"OneDrive"))
                       .resident(kAttrReparsePoint, reparse(0x9000701A)).finish());
    add(index, 38, RecordBuilder(kInUse).resident(kAttrFileName, file_name(37, 1, u"photo.jpg"))
                       .resident(kAttrData, bytes_of_size(400)).finish());
    // frag.dat keeps its $DATA in an extension record
    add(index, 40, RecordBuilder(kInUse).resident(kAttrStandardInformation, standard_info())
                       .resident(kAttrFileName, file_name(kRootFrn, 5, u"frag.dat")).finish());
    add(index, 41, RecordBuilder(kInUse, 1, 40).non_resident_data(4096, 777, {0x11, 0x01, 0x60, 0x00}).finish());
    // A file symlink: recorded, flagged as a link, never followed
    add(index, 42, RecordBuilder(kInUse).resident(kAttrStandardInformation, standard_info(0x420))
                       .resident(kAttrFileName, file_name(kRootFrn, 5, u"file-link"))
                       .resident(kAttrReparsePoint, reparse(0xA000000C)).finish());

    NodeStore store;
    std::atomic<bool> cancel{false};
    uint64_t files = 0, dirs = 0, bytes = 0;
    REQUIRE(index.build_tree(kRootFrn, u"C:\\", store, cancel, files, dirs, bytes));
    store.finalize();

    const auto& root = store.nodes()[0];
    CHECK(name_of(store, 0) == u"C:\\");
    CHECK(root.size == 5000 + 1000 + 400 + 777);
    CHECK(root.file_count == 6); // a.txt, big.bin, link.bin, photo.jpg, frag.dat, file-link
    CHECK(root.dir_count == 3);  // docs, Junction, OneDrive
    CHECK(files == 6);
    CHECK(dirs == 3);

    CHECK(child_named(store, 0, u"deleted.tmp") == ES_NONE);
    const uint32_t docs = child_named(store, 0, u"docs");
    REQUIRE(docs != ES_NONE);
    CHECK(child_named(store, docs, u"orphan.txt") == ES_NONE);
    const uint32_t link = child_named(store, docs, u"link.bin");
    REQUIRE(link != ES_NONE);
    CHECK((store.nodes()[link].flags & ES_NODE_HARDLINK) != 0);
    CHECK(store.nodes()[link].size == 0);
    const uint32_t big = child_named(store, 0, u"big.bin");
    REQUIRE(big != ES_NONE);
    CHECK((store.nodes()[big].flags & ES_NODE_HAS_LINKS) != 0); // counted name of a linked file
    const uint32_t file_link = child_named(store, 0, u"file-link");
    REQUIRE(file_link != ES_NONE);
    CHECK((store.nodes()[file_link].flags & ES_NODE_REPARSE) != 0);

    const uint32_t junction = child_named(store, 0, u"Junction");
    REQUIRE(junction != ES_NONE);
    CHECK((store.nodes()[junction].flags & ES_NODE_REPARSE) != 0);
    const uint32_t onedrive = child_named(store, 0, u"OneDrive");
    REQUIRE(onedrive != ES_NONE);
    CHECK((store.nodes()[onedrive].flags & ES_NODE_REPARSE) == 0);
    CHECK(store.nodes()[onedrive].size == 400);

    const uint32_t frag = child_named(store, 0, u"frag.dat");
    REQUIRE(frag != ES_NONE);
    CHECK(store.nodes()[frag].size == 777);
    CHECK(store.nodes()[frag].mtime == kUnixMs2024);

    // Largest first after finalize
    CHECK(name_of(store, root.first_child) == u"big.bin");
}

TEST_CASE("a subfolder can be the root") {
    MftIndex index(40);
    add(index, kRootFrn, RecordBuilder(kDir, 5).resident(kAttrFileName, file_name(kRootFrn, 5, u".")).finish());
    add(index, 30, RecordBuilder(kDir, 2).resident(kAttrFileName, file_name(kRootFrn, 5, u"docs")).finish());
    add(index, 31, RecordBuilder(kInUse).resident(kAttrFileName, file_name(30, 2, u"a.txt"))
                       .resident(kAttrData, bytes_of_size(10)).finish());
    add(index, 32, RecordBuilder(kInUse).resident(kAttrFileName, file_name(kRootFrn, 5, u"outside.txt"))
                       .resident(kAttrData, bytes_of_size(99)).finish());

    NodeStore store;
    std::atomic<bool> cancel{false};
    uint64_t files, dirs, bytes;
    REQUIRE(index.build_tree(30, u"C:\\docs", store, cancel, files, dirs, bytes));
    store.finalize();
    CHECK(store.nodes()[0].size == 10);
    CHECK(store.nodes().size() == 2);
    CHECK_FALSE(index.build_tree(31, u"C:\\docs\\a.txt", store, cancel, files, dirs, bytes)); // not a directory
}

TEST_CASE("dates beyond the year 9999 are clamped") {
    std::vector<uint8_t> si(48, 0);
    put<int64_t>(si, 8, INT64_MAX);  // NTFS can store dates up to the year 30828
    put<int64_t>(si, 24, INT64_MAX);
    auto record = RecordBuilder(kInUse).resident(kAttrStandardInformation, si).finish();
    REQUIRE(apply_fixups(record.data(), record.size()));
    ParsedRecord r;
    REQUIRE(parse_record(record.data(), record.size(), r));
    CHECK(r.mtime == emmascan::kMaxUnixMs);
    CHECK(r.atime == emmascan::kMaxUnixMs);
}
