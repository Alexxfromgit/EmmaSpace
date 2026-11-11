#pragma once
// Decodes the records getattrlistbulk(2) returns (macOS). Portable on purpose: it uses its own copies of
// the stable <sys/attr.h> constants (checked against the real ones on macOS), so the tests also run on
// Linux and Windows CI, the way the ext4 parser's do.
//
// Record layout, as the kernel packs it when FSOPT_PACK_INVAL_ATTRS is not set:
//   u32 length (whole record)  attribute_set_t returned  [u32 error]  then each returned attribute in bit
//   order, common attributes first, then directory, then file attributes, then (with
//   FSOPT_ATTR_CMN_EXTENDED) the extended common ones in the fork slot. Only attributes whose bit is in
//   `returned` are present. Values are 4-byte aligned, so 8-byte values are read with memcpy.

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string_view>

namespace emmascan::bulk {

// <sys/attr.h> attribute bits this decoder reads.
inline constexpr uint32_t kCmnName = 0x00000001u;
inline constexpr uint32_t kCmnDevId = 0x00000002u;
inline constexpr uint32_t kCmnObjType = 0x00000008u;
inline constexpr uint32_t kCmnModTime = 0x00000400u;
inline constexpr uint32_t kCmnAccTime = 0x00001000u;
inline constexpr uint32_t kCmnAccessMask = 0x00020000u;
inline constexpr uint32_t kCmnFlags = 0x00040000u;
inline constexpr uint32_t kCmnFileId = 0x02000000u;
inline constexpr uint32_t kCmnError = 0x20000000u;
inline constexpr uint32_t kCmnReturnedAttrs = 0x80000000u;
inline constexpr uint32_t kDirMountStatus = 0x00000004u;
inline constexpr uint32_t kFileLinkCount = 0x00000001u;
inline constexpr uint32_t kFileAllocSize = 0x00000004u;
inline constexpr uint32_t kFileDataLength = 0x00000200u;
inline constexpr uint32_t kFileDataAllocSize = 0x00000400u; // the data fork alone (ALLOCSIZE covers every fork)
// ATTR_CMNEXT_*: extended common attributes, passed in the fork bitmap with FSOPT_ATTR_CMN_EXTENDED.
inline constexpr uint32_t kExtPrivateSize = 0x00000008u; // bytes not shared with any other file (decoded, not requested)
inline constexpr uint32_t kExtCloneId = 0x00000100u;     // the same for every pure clone of a file
inline constexpr uint32_t kExtFlags = 0x00000200u;       // EF_* below

// ATTR_CMNEXT_EXT_FLAGS values (APFS clones).
inline constexpr uint64_t kMayShareBlocks = 0x00000001u; // EF_MAY_SHARE_BLOCKS: some blocks may be shared
inline constexpr uint64_t kSharesAllBlocks = 0x00000040u; // EF_SHARES_ALL_BLOCKS: a pure clone

// What the walker asks for. Everything else is left out so records stay small.
inline constexpr uint32_t kCommonRequested = kCmnReturnedAttrs | kCmnName | kCmnDevId | kCmnObjType | kCmnModTime |
                                             kCmnAccTime | kCmnAccessMask | kCmnFlags | kCmnFileId | kCmnError;
inline constexpr uint32_t kDirRequested = kDirMountStatus;
inline constexpr uint32_t kFileRequested = kFileLinkCount | kFileAllocSize | kFileDataLength | kFileDataAllocSize;
// Not the private size: APFS works it out from the extents, which made listing ~70% slower. A pure clone
// shares every block, so it owns nothing alone anyway.
inline constexpr uint32_t kExtRequested = kExtCloneId | kExtFlags;

// Without these the entry is described with fstatat() instead.
inline constexpr uint32_t kCommonNeeded =
    kCmnName | kCmnDevId | kCmnObjType | kCmnModTime | kCmnAccTime | kCmnAccessMask | kCmnFlags | kCmnFileId;
inline constexpr uint32_t kFileNeeded = kFileLinkCount | kFileAllocSize | kFileDataLength;
// A folder whose mount status is unknown could be a mount point (whose record describes the covered folder)
// or an autofs trigger, so its record alone is not trusted.
inline constexpr uint32_t kDirNeeded = kDirMountStatus;

// fsobj_type_t (<sys/vnode.h>) and ATTR_DIR_MOUNTSTATUS values.
inline constexpr uint32_t kTypeDirectory = 2; // VDIR
inline constexpr uint32_t kMountPoint = 0x1u; // DIR_MNTSTATUS_MNTPOINT
inline constexpr uint32_t kMountTrigger = 0x2u; // DIR_MNTSTATUS_TRIGGER (autofs: looking inside mounts it)

// st_flags bit of a firmlink (<sys/stat.h>): the record describes the link, not the folder it leads to.
inline constexpr uint32_t kFlagFirmlink = 0x00800000u; // SF_FIRMLINK
// st_flags bit of a data vault, a folder macOS protects even from root; stat() on it fails with EPERM.
inline constexpr uint32_t kFlagDataVault = 0x00000080u; // UF_DATAVAULT

// st_mode type bits for each fsobj_type_t (VNON, VREG, VDIR, VBLK, VCHR, VLNK, VSOCK, VFIFO). getattrlist(2)
// promises only the permission bits of ATTR_CMN_ACCESSMASK, so the type comes from ATTR_CMN_OBJTYPE.
inline constexpr uint32_t kModeTypes[] = {0, 0100000, 0040000, 0060000, 0020000, 0120000, 0140000, 0010000};
constexpr uint32_t mode_type_of(uint32_t obj_type) {
    return obj_type < sizeof(kModeTypes) / sizeof(kModeTypes[0]) ? kModeTypes[obj_type] : 0;
}

struct Time {
    int64_t sec = 0;
    int64_t nsec = 0;
};

struct Record {
    std::string_view name; // points into the buffer; never empty, ".", ".." or containing '/'
    uint32_t error = 0;    // ATTR_CMN_ERROR: the entry is there but could not be described
    uint32_t common = 0;   // returned bits
    uint32_t dir = 0;
    uint32_t file = 0;
    int32_t dev = 0;
    uint32_t obj_type = 0;
    Time mtime, atime;
    uint32_t mode = 0;     // ATTR_CMN_ACCESSMASK: only its permission bits are reliable, see st_mode()
    uint32_t flags = 0;    // same as st_flags
    uint64_t file_id = 0;
    uint32_t mount_status = 0;
    uint32_t link_count = 0;
    int64_t alloc_size = 0;
    int64_t data_length = 0;
    int64_t data_alloc_size = 0;
    uint32_t ext = 0;      // returned extended (ATTR_CMNEXT_*) bits
    int64_t private_size = 0;
    uint64_t clone_id = 0;
    uint64_t ext_flags = 0;

    bool is_directory() const { return obj_type == kTypeDirectory; }

    // st_mode as stat() reports it: permission bits from the access mask, the type from the object type.
    uint32_t st_mode() const { return (mode & 07777u) | mode_type_of(obj_type); }

    // A file that may share blocks with others (an APFS clone, or a clone that was changed since).
    bool may_share_blocks() const { return (ext & kExtFlags) && (ext_flags & kMayShareBlocks); }
    // Every block is shared with the other files of the same clone id, so none of them owns any alone.
    bool is_pure_clone() const { return may_share_blocks() && (ext_flags & kSharesAllBlocks) && (ext & kExtCloneId); }

    // True when every attribute the walker needs was returned and the type is a known one. If not, the
    // caller falls back to fstatat().
    bool complete() const {
        if ((common & kCommonNeeded) != kCommonNeeded || mode_type_of(obj_type) == 0) return false;
        return is_directory() ? (dir & kDirNeeded) == kDirNeeded : (file & kFileNeeded) == kFileNeeded;
    }
};

enum class Decode {
    ok,
    skip,     // a well-formed record to ignore (bad name); move on by `length`
    malformed // the buffer cannot be trusted from here on; stop reading this batch
};

namespace detail {

class Reader {
public:
    Reader(const char* begin, const char* end) : p_(begin), end_(end) {}

    template <class T>
    bool read(T& value) {
        if (static_cast<size_t>(end_ - p_) < sizeof(T)) return false;
        std::memcpy(&value, p_, sizeof(T));
        p_ += sizeof(T);
        return true;
    }
    const char* position() const { return p_; }

private:
    const char* p_;
    const char* end_;
};

} // namespace detail

// Decodes the record at `data`, where `available` bytes of the batch are left. On ok/skip, `length` is the
// record's size, so the next record starts at data + length.
inline Decode decode_record(const char* data, size_t available, Record& out, uint32_t& length) {
    out = Record{};
    length = 0;
    // Smallest valid record: the length word and the returned attribute set (5 u32).
    constexpr size_t kHeader = sizeof(uint32_t) * 6;
    if (available < kHeader) return Decode::malformed;
    std::memcpy(&length, data, sizeof length);
    if (length < kHeader || length > available) return Decode::malformed;

    detail::Reader r(data + sizeof(uint32_t), data + length);
    uint32_t returned[5]{}; // common, vol, dir, file, fork
    for (auto& word : returned)
        if (!r.read(word)) return Decode::malformed;
    out.common = returned[0];
    out.dir = returned[2];
    out.file = returned[3];
    out.ext = returned[4];

    // Anything outside what was asked for would shift every later field.
    if ((out.common & ~kCommonRequested) != 0 || returned[1] != 0 || (out.dir & ~kDirRequested) != 0 ||
        (out.file & ~kFileRequested) != 0 || (out.ext & ~kExtRequested) != 0)
        return Decode::malformed;

    if ((out.common & kCmnError) && !r.read(out.error)) return Decode::malformed;

    if (out.common & kCmnName) {
        const char* ref_at = r.position();
        int32_t offset = 0;
        uint32_t name_length = 0;
        if (!r.read(offset) || !r.read(name_length)) return Decode::malformed;
        // The offset is relative to the attrreference_t itself; the length includes the terminating NUL.
        const ptrdiff_t start = (ref_at - data) + offset;
        if (offset < 0 || name_length == 0 || start < 0 || static_cast<size_t>(start) + name_length > length)
            return Decode::malformed;
        const char* name = data + start;
        const size_t n = strnlen(name, name_length);
        if (n == name_length) return Decode::malformed; // no NUL inside the stated length
        out.name = std::string_view(name, n);
    }

    if ((out.common & kCmnDevId) && !r.read(out.dev)) return Decode::malformed;
    if ((out.common & kCmnObjType) && !r.read(out.obj_type)) return Decode::malformed;
    if ((out.common & kCmnModTime) && (!r.read(out.mtime.sec) || !r.read(out.mtime.nsec))) return Decode::malformed;
    if ((out.common & kCmnAccTime) && (!r.read(out.atime.sec) || !r.read(out.atime.nsec))) return Decode::malformed;
    if ((out.common & kCmnAccessMask) && !r.read(out.mode)) return Decode::malformed;
    if ((out.common & kCmnFlags) && !r.read(out.flags)) return Decode::malformed;
    if ((out.common & kCmnFileId) && !r.read(out.file_id)) return Decode::malformed;
    if ((out.dir & kDirMountStatus) && !r.read(out.mount_status)) return Decode::malformed;
    if ((out.file & kFileLinkCount) && !r.read(out.link_count)) return Decode::malformed;
    if ((out.file & kFileAllocSize) && !r.read(out.alloc_size)) return Decode::malformed;
    if ((out.file & kFileDataLength) && !r.read(out.data_length)) return Decode::malformed;
    if ((out.file & kFileDataAllocSize) && !r.read(out.data_alloc_size)) return Decode::malformed;
    if ((out.ext & kExtPrivateSize) && !r.read(out.private_size)) return Decode::malformed;
    if ((out.ext & kExtCloneId) && !r.read(out.clone_id)) return Decode::malformed;
    if ((out.ext & kExtFlags) && !r.read(out.ext_flags)) return Decode::malformed;

    // A name is joined into paths, so anything that could step outside the folder is refused.
    if (out.name.empty() || out.name == "." || out.name == ".." || out.name.find('/') != std::string_view::npos)
        return Decode::skip;
    return Decode::ok;
}

} // namespace emmascan::bulk
