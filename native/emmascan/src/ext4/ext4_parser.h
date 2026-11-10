#pragma once

// Portable ext2/ext3/ext4 metadata reading: no OS APIs, so it can be unit tested with images made by
// mke2fs. Layout reference: the kernel's "ext4 Data Structures and Algorithms" documentation
// (Documentation/filesystems/ext4/) and fs/ext4/ext4.h.
//
// A scan reads every inode table, then every directory block, in disk order, instead of opening
// each folder: the ext4 counterpart of reading the NTFS master file table.

#include "../node_store.h"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace emmascan::ext4 {

constexpr uint32_t kRootIno = 2;

// Random-access reads of the file system's bytes (a block device or an image file).
// Must be safe to call from several threads at once.
class BlockSource {
public:
    virtual ~BlockSource() = default;
    // Reads exactly `length` bytes at `offset`. False on any failure or short read.
    virtual bool read(uint64_t offset, void* buffer, size_t length) = 0;
};

// Superblock: bytes 1024..2047 of the file system.
constexpr size_t kSuperblockOffset = 1024;
constexpr size_t kSuperblockSize = 1024;

struct Superblock {
    uint32_t block_size = 0;
    uint64_t block_count = 0;
    uint32_t inode_count = 0;
    uint32_t inodes_per_group = 0;
    uint32_t blocks_per_group = 0;
    uint32_t first_data_block = 0;
    uint32_t group_count = 0;
    uint16_t inode_size = 0;
    uint16_t desc_size = 0;
    uint32_t first_meta_bg = 0;
    uint32_t backup_bgs[2] = {}; // sparse_super2
    uint32_t feature_compat = 0;
    uint32_t feature_incompat = 0;
    uint32_t feature_ro_compat = 0;
    uint8_t uuid[16] = {};

    bool has_64bit() const { return (feature_incompat & 0x80) != 0; }
    bool has_meta_bg() const { return (feature_incompat & 0x10) != 0; }
    bool has_largedir() const { return (feature_incompat & 0x4000) != 0; }
    bool has_filetype() const { return (feature_incompat & 0x2) != 0; }
    bool has_huge_file() const { return (feature_ro_compat & 0x8) != 0; }
    bool has_sparse_super() const { return (feature_ro_compat & 0x1) != 0; }
    // GDT_CSUM or METADATA_CSUM: only then are the "uninitialized" group hints trustworthy.
    bool has_group_checksums() const { return (feature_ro_compat & (0x10 | 0x400)) != 0; }
};

enum class SuperblockCheck {
    Ok,
    NotExt,      // no ext2/3/4 magic or nonsensical geometry
    Unsupported, // a feature this reader does not handle (e.g. inline_data); walk instead
};

SuperblockCheck parse_superblock(const uint8_t* data, size_t length, Superblock& out);

struct GroupDesc {
    uint64_t inode_bitmap = 0; // block of the group's inode allocation bitmap
    uint64_t inode_table = 0;  // first block of the group's inode table
    uint32_t itable_unused = 0;
    uint16_t flags = 0;
};
constexpr uint16_t kBgInodeUninit = 0x1;

// Block that holds group descriptor number `group` (handles meta_bg).
uint64_t descriptor_block(const Superblock& sb, uint32_t group);

// One inode, decoded.
struct Inode {
    uint16_t mode = 0;
    uint16_t links = 0;
    uint32_t flags = 0;
    uint64_t size = 0;
    uint64_t allocated = 0; // bytes, as st_blocks * 512 would report
    int64_t mtime = 0;      // Unix ms
    int64_t atime = 0;
    uint8_t block[60] = {}; // i_block: extent tree root, block map or inline data
};
constexpr uint32_t kFlagEncrypt = 0x800;
constexpr uint32_t kFlagHugeFile = 0x40000;
constexpr uint32_t kFlagExtents = 0x80000;
constexpr uint32_t kFlagInlineData = 0x10000000;
constexpr uint32_t kFlagCasefold = 0x40000000;

// Decodes the inode at `data` (`inode_size` bytes). False if it is unused.
bool parse_inode(const uint8_t* data, const Superblock& sb, Inode& out);

// Visits each directory entry of one directory block: fn(child_inode, name). Returns false if the
// block is malformed (entries seen before the damage are still reported) or holds an entry whose name
// is not a valid path component (it is skipped). "." and ".." are skipped.
template <class Fn>
bool parse_dir_block(const uint8_t* block, uint32_t block_size, bool has_filetype, Fn&& fn);

struct BlockLimits {
    uint64_t max_logical; // logical block numbers below this (the folder's size in blocks)
    uint64_t max_count;   // at most this many blocks (what is allocated to it)
};

// Physical blocks of a directory, in logical order. Reads extent index or indirect blocks through `src`.
// False on damage (including more blocks mapped than `limits.max_count`); `out` then holds those found so far.
bool directory_blocks(BlockSource& src, const Superblock& sb, const Inode& dir, BlockLimits limits,
                      std::vector<uint64_t>& out);

struct ReadOptions {
    unsigned threads = 0; // 0 = automatic
    const std::atomic<bool>* cancel = nullptr;
    std::atomic<uint64_t>* inodes_read = nullptr; // progress
};

// Everything a scan needs from one file system: every used inode and every directory entry.
class Ext4Index {
public:
    // Reads the inode tables and directory blocks. ES_ERR_NOT_SUPPORTED when this is not a file system
    // this reader handles, ES_ERR_IO on a failed read, ES_ERR_CANCELLED, ES_ERR_NO_MEMORY.
    es_status load(BlockSource& src, const ReadOptions& options = {});

    const Superblock& superblock() const { return sb_; }

    // Builds the subtree below directory inode `root_ino`. A file with several hard links is counted under
    // the first of its names met in that subtree; its other names appear flagged ES_NODE_HARDLINK with no
    // size. `skip_dirs` lists folder inodes that are mount points: they are recorded flagged ES_NODE_REPARSE and
    // not descended. Returns false if cancelled or if root_ino is not a folder.
    bool build_tree(uint32_t root_ino, std::u16string_view root_name,
                    const std::vector<uint32_t>& skip_dirs, NodeStore& store,
                    const std::atomic<bool>& cancel, uint64_t& files, uint64_t& dirs, uint64_t& bytes,
                    uint32_t& errors) const;

    // Inode of `name` in directory `dir_ino`, or 0. In a casefold folder (chattr +F) ASCII letters match in either
    // case, as the kernel does (it also folds other Unicode letters; those must match exactly here).
    uint32_t find_child(uint32_t dir_ino, std::string_view name) const;

    size_t inode_count() const { return inodes_.size(); }
    size_t entry_count() const { return links_.size(); }

private:
    struct Entry {
        uint32_t ino = 0;
        uint16_t mode = 0;
        uint16_t links = 0;
        uint32_t flags = 0;
        uint64_t size = 0;
        uint64_t allocated = 0;
        int64_t mtime = 0;
        int64_t atime = 0;
    };
    struct Link {
        uint32_t parent; // directory, as an index into dirs_
        uint32_t child;  // inode
        uint32_t name_offset;
        uint16_t name_length;
    };

    const Entry* find(uint32_t ino) const;
    uint32_t dir_index(uint32_t ino) const; // index into dirs_, or UINT32_MAX

    Superblock sb_;
    std::vector<Entry> inodes_;          // used inodes, ascending
    std::vector<uint32_t> group_start_;  // index into inodes_ of each group's first entry (+1 sentinel)
    std::vector<uint32_t> dirs_;         // directory inodes, ascending
    std::vector<uint32_t> damaged_dirs_; // directories with a block that could not be mapped or parsed, ascending
    std::vector<Link> links_;
    std::vector<uint32_t> dir_start_;    // links_ of dirs_[d] are [dir_start_[d], dir_start_[d + 1])
    std::string names_; // UTF-8, as stored on disk
};

// ---- template implementation

template <class Fn>
bool parse_dir_block(const uint8_t* block, uint32_t block_size, bool has_filetype, Fn&& fn) {
    uint32_t pos = 0;
    bool valid = true;
    while (pos + 8 <= block_size) {
        const uint8_t* d = block + pos;
        const uint32_t ino = static_cast<uint32_t>(d[0]) | (static_cast<uint32_t>(d[1]) << 8) |
                             (static_cast<uint32_t>(d[2]) << 16) | (static_cast<uint32_t>(d[3]) << 24);
        uint32_t rec_len = static_cast<uint32_t>(d[4]) | (static_cast<uint32_t>(d[5]) << 8);
        // 64 KiB blocks: a whole-block record is stored as 0 or 65535 (fs/ext4/ext4.h ext4_rec_len_from_disk).
        if (block_size >= 65536 && (rec_len == 65535 || rec_len == 0)) rec_len = 65536;
        const uint32_t name_len = has_filetype ? d[6] : (static_cast<uint32_t>(d[6]) | (static_cast<uint32_t>(d[7]) << 8));
        if (rec_len < 8 || (rec_len & 3) != 0 || rec_len > block_size - pos || name_len + 8 > rec_len) return false;
        if (ino != 0 && name_len > 0) {
            const std::string_view name(reinterpret_cast<const char*>(d + 8), name_len);
            // A name is one path component: never "/" or NUL inside, at most 255 bytes (EXT4_NAME_LEN). Anything
            // else is damage or a crafted entry; passing it on would let a path built from it leave the folder.
            if (name_len > 255 || name.find('/') != std::string_view::npos || name.find('\0') != std::string_view::npos)
                valid = false;
            else if (name != "." && name != "..")
                fn(ino, name);
        }
        pos += rec_len;
    }
    return valid && pos == block_size;
}

} // namespace emmascan::ext4
