// decode_record() on synthetic getattrlistbulk records. The decoder is portable, so these run on every
// platform; on macOS the real kernel output is covered by the scan tests.

#include "posix/attr_bulk.h"

#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <cstring>
#include <random>
#include <string>
#include <vector>

using namespace emmascan::bulk;

namespace {

// Packs one record the way the kernel does without FSOPT_PACK_INVAL_ATTRS: length, returned set, then
// the returned attributes in bit order, the name's bytes after the fixed part, padded to 4 bytes.
struct RecordBuilder {
    uint32_t common = kCmnReturnedAttrs | kCommonNeeded;
    uint32_t dir = 0;
    uint32_t file = kFileNeeded;
    uint32_t error = 0;
    std::string name = "file.txt";
    int32_t dev = 7;
    uint32_t obj_type = 1; // VREG
    Time mtime{1700000000, 5}, atime{1700000001, 6};
    uint32_t mode = 0100644;
    uint32_t flags = 0;
    uint64_t file_id = 42;
    uint32_t mount_status = 0;
    uint32_t link_count = 1;
    int64_t alloc_size = 8192;
    int64_t data_length = 5000;

    std::vector<char> build() const {
        std::vector<char> out(4, 0); // length, filled in at the end
        auto put = [&out](const auto& v) { // resize + memcpy: GCC 13 warns (wrongly) about insert() here
            const size_t at = out.size();
            out.resize(at + sizeof(v));
            std::memcpy(out.data() + at, &v, sizeof(v));
        };
        const uint32_t returned[5] = {common, 0, dir, file, 0};
        for (uint32_t w : returned) put(w);
        if (common & kCmnError) put(error);
        size_t name_ref = 0;
        if (common & kCmnName) {
            name_ref = out.size();
            put(int32_t{0});
            put(uint32_t{0});
        }
        if (common & kCmnDevId) put(dev);
        if (common & kCmnObjType) put(obj_type);
        if (common & kCmnModTime) { put(mtime.sec); put(mtime.nsec); }
        if (common & kCmnAccTime) { put(atime.sec); put(atime.nsec); }
        if (common & kCmnAccessMask) put(mode);
        if (common & kCmnFlags) put(flags);
        if (common & kCmnFileId) put(file_id);
        if (dir & kDirMountStatus) put(mount_status);
        if (file & kFileLinkCount) put(link_count);
        if (file & kFileAllocSize) put(alloc_size);
        if (file & kFileDataLength) put(data_length);
        if (common & kCmnName) {
            const auto offset = static_cast<int32_t>(out.size() - name_ref);
            const auto length = static_cast<uint32_t>(name.size() + 1);
            std::memcpy(out.data() + name_ref, &offset, 4);
            std::memcpy(out.data() + name_ref + 4, &length, 4);
            out.insert(out.end(), name.begin(), name.end());
            out.push_back('\0');
        }
        while (out.size() % 4) out.push_back('\0');
        const auto total = static_cast<uint32_t>(out.size());
        std::memcpy(out.data(), &total, 4);
        return out;
    }
};

Decode decode(const std::vector<char>& buf, Record& r, uint32_t& length) {
    return decode_record(buf.data(), buf.size(), r, length);
}

} // namespace

TEST_CASE("bulk: a file record yields every field") {
    const auto buf = RecordBuilder{}.build();
    Record r;
    uint32_t length = 0;
    REQUIRE(decode(buf, r, length) == Decode::ok);
    CHECK(length == buf.size());
    CHECK(r.name == "file.txt");
    CHECK(r.dev == 7);
    CHECK(r.obj_type == 1);
    CHECK(r.mtime.sec == 1700000000);
    CHECK(r.mtime.nsec == 5);
    CHECK(r.atime.sec == 1700000001);
    CHECK(r.mode == 0100644);
    CHECK(r.file_id == 42);
    CHECK(r.link_count == 1);
    CHECK(r.alloc_size == 8192);
    CHECK(r.data_length == 5000);
    CHECK(r.complete());
    CHECK_FALSE(r.is_directory());
}

TEST_CASE("bulk: a directory record carries its mount status and needs no file attributes") {
    RecordBuilder b;
    b.obj_type = kTypeDirectory;
    b.mode = 040755;
    b.dir = kDirMountStatus;
    b.file = 0;
    b.mount_status = kMountTrigger;
    b.name = "Network";
    const auto buf = b.build(); // r.name points into it
    Record r;
    uint32_t length = 0;
    REQUIRE(decode(buf, r, length) == Decode::ok);
    CHECK(r.is_directory());
    CHECK(r.complete());
    CHECK(r.mount_status == kMountTrigger);
    CHECK(r.name == "Network");
}

TEST_CASE("bulk: records follow each other by their length") {
    RecordBuilder a, b;
    b.name = "second";
    b.file_id = 43;
    auto buf = a.build();
    const auto second = b.build();
    buf.insert(buf.end(), second.begin(), second.end());

    Record r;
    uint32_t length = 0;
    REQUIRE(decode_record(buf.data(), buf.size(), r, length) == Decode::ok);
    CHECK(r.name == "file.txt");
    REQUIRE(decode_record(buf.data() + length, buf.size() - length, r, length) == Decode::ok);
    CHECK(r.name == "second");
    CHECK(r.file_id == 43);
}

TEST_CASE("bulk: the file type comes from the object type, not the access mask") {
    RecordBuilder b;
    b.mode = 0644;          // permission bits only, as getattrlist(2) promises
    b.obj_type = 5;         // VLNK
    const auto buf = b.build();
    Record r;
    uint32_t length = 0;
    REQUIRE(decode(buf, r, length) == Decode::ok);
    CHECK(r.st_mode() == (0120000u | 0644u));
    CHECK(r.complete());

    b.mode = 0100755;       // type bits that disagree with the object type are dropped
    b.obj_type = kTypeDirectory;
    b.dir = kDirMountStatus;
    b.file = 0;
    const auto buf2 = b.build();
    REQUIRE(decode(buf2, r, length) == Decode::ok);
    CHECK(r.st_mode() == (0040000u | 0755u));

    b.obj_type = 99;        // unknown type: described with fstatat() instead
    const auto buf3 = b.build();
    REQUIRE(decode(buf3, r, length) == Decode::ok);
    CHECK_FALSE(r.complete());
}

TEST_CASE("bulk: an entry with an error is reported, not trusted") {
    RecordBuilder b;
    b.common = kCmnReturnedAttrs | kCmnError | kCmnName;
    b.file = 0;
    b.error = 1; // EPERM
    const auto buf = b.build();
    Record r;
    uint32_t length = 0;
    REQUIRE(decode(buf, r, length) == Decode::ok);
    CHECK(r.error == 1);
    CHECK(r.name == "file.txt");
    CHECK_FALSE(r.complete()); // the walker asks fstatat() instead
}

TEST_CASE("bulk: missing attributes send the entry to fstatat") {
    RecordBuilder b;
    b.file = kFileLinkCount | kFileDataLength; // no allocated size
    Record r;
    uint32_t length = 0;
    REQUIRE(decode(b.build(), r, length) == Decode::ok);
    CHECK_FALSE(r.complete());
}

TEST_CASE("bulk: names that could leave the folder are skipped") {
    for (const char* bad : {".", "..", "a/b", "/"}) {
        RecordBuilder b;
        b.name = bad;
        Record r;
        uint32_t length = 0;
        CHECK(decode(b.build(), r, length) == Decode::skip);
        CHECK(length > 0); // the next record can still be found
    }
    RecordBuilder empty;
    empty.name = "";
    Record r;
    uint32_t length = 0;
    CHECK(decode(empty.build(), r, length) == Decode::skip);
}

TEST_CASE("bulk: broken lengths and offsets are refused") {
    const auto good = RecordBuilder{}.build();
    Record r;
    uint32_t length = 0;

    SECTION("shorter than its header") {
        CHECK(decode_record(good.data(), 8, r, length) == Decode::malformed);
    }
    SECTION("length past the end of the batch") {
        auto buf = good;
        const uint32_t huge = static_cast<uint32_t>(buf.size() + 4);
        std::memcpy(buf.data(), &huge, 4);
        CHECK(decode(buf, r, length) == Decode::malformed);
    }
    SECTION("length smaller than the attributes it holds") {
        auto buf = good;
        const uint32_t tiny = 32;
        std::memcpy(buf.data(), &tiny, 4);
        CHECK(decode(buf, r, length) == Decode::malformed);
    }
    SECTION("name offset outside the record") {
        auto buf = good;
        const int32_t offset = 4096;
        std::memcpy(buf.data() + 24, &offset, 4); // the attrreference follows length + returned set
        CHECK(decode(buf, r, length) == Decode::malformed);
    }
    SECTION("negative name offset") {
        auto buf = good;
        const int32_t offset = -24;
        std::memcpy(buf.data() + 24, &offset, 4);
        CHECK(decode(buf, r, length) == Decode::malformed);
    }
    SECTION("name without its terminating NUL") {
        auto buf = good;
        uint32_t name_length = 0;
        std::memcpy(&name_length, buf.data() + 28, 4);
        std::memset(buf.data() + buf.size() - 4, 'x', 4); // overwrite the NUL and the padding
        const int32_t offset = static_cast<int32_t>(buf.size() - 4 - 24);
        std::memcpy(buf.data() + 24, &offset, 4);
        const uint32_t four = 4;
        std::memcpy(buf.data() + 28, &four, 4);
        CHECK(decode(buf, r, length) == Decode::malformed);
    }
    SECTION("attributes nobody asked for") {
        RecordBuilder b;
        b.common |= 0x00000200u; // ATTR_CMN_CRTIME would shift every later field
        CHECK(decode(b.build(), r, length) == Decode::malformed);
    }
    SECTION("volume or fork attributes") {
        auto buf = good;
        const uint32_t vol = 1;
        std::memcpy(buf.data() + 8, &vol, 4);
        CHECK(decode(buf, r, length) == Decode::malformed);
    }
}

TEST_CASE("bulk: damaged batches never read outside the buffer") {
    // Random corruption and truncation of a valid batch must only ever give ok, skip or malformed; run
    // the tests under ASan (see CONTRIBUTING) to catch any read outside the buffer.
    std::vector<char> batch;
    for (int i = 0; i < 8; ++i) {
        RecordBuilder b;
        b.name = std::string(static_cast<size_t>(i * 7 + 1), static_cast<char>('a' + i));
        if (i % 3 == 0) {
            b.obj_type = kTypeDirectory;
            b.dir = kDirMountStatus;
            b.file = 0;
        }
        const auto rec = b.build();
        batch.insert(batch.end(), rec.begin(), rec.end());
    }
    std::mt19937 rng(1234);
    for (int round = 0; round < 20000; ++round) {
        std::vector<char> buf = batch;
        const int flips = 1 + static_cast<int>(rng() % 8);
        for (int k = 0; k < flips; ++k) buf[rng() % buf.size()] = static_cast<char>(rng());
        buf.resize(rng() % (buf.size() + 1));
        // Own allocation of exactly the truncated size, so ASan sees any read past it.
        std::vector<char> exact(buf.begin(), buf.end());
        const char* p = exact.data();
        size_t left = exact.size();
        for (int guard = 0; guard < 64 && left > 0; ++guard) {
            Record r;
            uint32_t length = 0;
            const auto d = decode_record(p, left, r, length);
            if (d == Decode::malformed) break;
            REQUIRE(length > 0);
            REQUIRE(length <= left);
            if (d == Decode::ok) {
                CHECK_FALSE(r.name.empty());
                CHECK(r.name.data() >= exact.data());
                CHECK(r.name.data() + r.name.size() <= exact.data() + exact.size());
            }
            p += length;
            left -= length;
        }
    }
}
