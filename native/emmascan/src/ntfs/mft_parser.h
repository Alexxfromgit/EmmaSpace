#pragma once

// Portable NTFS master file table parsing: no Windows APIs, so it can be unit tested with
// synthetic records. Layout references: the NTFS on-disk format as documented by the
// Linux-NTFS project (FILE record header, attribute headers, runlists, update sequence arrays).

#include "../node_store.h"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <string_view>
#include <vector>

namespace emmascan::ntfs {

constexpr uint64_t kFrnMask = 0x0000FFFFFFFFFFFFull; // file reference: low 48 bits = record number
constexpr uint32_t kRootFrn = 5;                     // the volume's root directory

constexpr uint32_t kAttrStandardInformation = 0x10;
constexpr uint32_t kAttrFileName = 0x30;
constexpr uint32_t kAttrData = 0x80;
constexpr uint32_t kAttrReparsePoint = 0xC0;
constexpr uint32_t kAttrEnd = 0xFFFFFFFF;

constexpr uint8_t kNamespaceDos = 2;
constexpr uint32_t kFileAttributeReparsePoint = 0x400;
// FILE_ATTRIBUTE_OFFLINE | FILE_ATTRIBUTE_RECALL_ON_OPEN | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
constexpr uint32_t kCloudAttributes = 0x1000 | 0x40000 | 0x400000;

// Junctions, mount points and symlinks are "name surrogates"; cloud placeholders are not.
inline bool is_link_reparse_tag(uint32_t tag) { return (tag & 0x20000000u) != 0; }

struct DataRun {
    int64_t lcn;     // -1 for a sparse run
    uint64_t length; // in clusters
};

// Decodes a non-resident attribute's mapping pairs. Returns false on malformed input.
bool decode_runlist(const uint8_t* p, const uint8_t* end, std::vector<DataRun>& out);

// Verifies and undoes the update sequence array of a FILE record in place.
bool apply_fixups(uint8_t* record, size_t record_size, size_t stride = 512);

struct FileNameRef {
    uint64_t parent_ref; // full reference: sequence in the high 16 bits
    std::u16string_view name; // points into the record buffer
};

struct ParsedRecord {
    bool in_use = false;
    bool is_directory = false;
    uint16_t sequence = 0;
    uint64_t base_ref = 0; // non-zero for extension records

    bool has_standard_info = false;
    int64_t mtime = 0; // Unix ms
    int64_t atime = 0;
    uint32_t attributes = 0;

    bool has_data = false; // unnamed $DATA, first extent
    uint64_t data_size = 0;
    uint64_t allocated = 0;
    std::vector<DataRun> data_runs; // only filled when requested (for $MFT itself)

    uint32_t reparse_tag = 0;
    std::vector<FileNameRef> names; // DOS-only names are skipped
};

// Parses one FILE record (fixups must already be applied). Returns false if the record is
// not a valid FILE record; `out` is reset first.
bool parse_record(const uint8_t* record, size_t record_size, ParsedRecord& out, bool want_runs = false);

// Collects parsed records by record number (merging extension records into their base) and
// turns them into the flat tree of emmascan.h.
class MftIndex {
public:
    explicit MftIndex(uint64_t record_count);

    void add(uint32_t frn, const ParsedRecord& record);

    // Builds the subtree below `root_frn`. Hard links after the first appear as extra entries
    // flagged ES_NODE_HARDLINK with zero size, so totals count each file once.
    // Returns false if cancelled or if the root is not a directory in use.
    bool build_tree(uint32_t root_frn, std::u16string_view root_name, NodeStore& store,
                    const std::atomic<bool>& cancel, uint64_t& files, uint64_t& dirs, uint64_t& bytes) const;

    uint64_t record_count() const { return entries_.size(); }

private:
    struct Entry {
        uint64_t parent_frn = UINT64_MAX;
        uint64_t size = 0;
        uint64_t allocated = 0;
        int64_t mtime = 0;
        int64_t atime = 0;
        uint32_t attributes = 0;
        uint32_t name_offset = 0;
        uint32_t reparse_tag = 0;
        uint16_t name_length = 0;
        uint16_t sequence = 0;
        uint16_t parent_sequence = 0;
        uint8_t flags = 0;
    };
    struct ExtraLink {
        uint32_t frn;
        uint64_t parent_frn;
        uint16_t parent_sequence;
        uint32_t name_offset;
        uint16_t name_length;
    };
    static constexpr uint8_t kInUse = 1, kDirectory = 2, kHasName = 4, kHasInfo = 8;

    void add_name(uint32_t frn, const FileNameRef& name);
    bool valid_parent(uint64_t parent, uint16_t parent_sequence, uint32_t child) const;
    std::u16string_view name_at(uint32_t offset, uint16_t length) const {
        return {names_.data() + offset, length};
    }

    std::vector<Entry> entries_;
    std::vector<ExtraLink> links_;
    std::vector<char16_t> names_;
};

} // namespace emmascan::ntfs
