#include "ext4_parser.h"

#include "../time_util.h"
#include "../utf8.h"

#include <algorithm>
#include <cstring>
#include <new>
#include <set>
#include <thread>

namespace emmascan::ext4 {
namespace {

uint16_t le16(const uint8_t* p) { return static_cast<uint16_t>(p[0] | (p[1] << 8)); }
uint32_t le32(const uint8_t* p) {
    return static_cast<uint32_t>(p[0]) | (static_cast<uint32_t>(p[1]) << 8) | (static_cast<uint32_t>(p[2]) << 16) |
           (static_cast<uint32_t>(p[3]) << 24);
}

constexpr uint16_t kMagic = 0xEF53;
constexpr uint16_t kExtentMagic = 0xF30A;
constexpr int kMaxExtentDepth = 5;

// INCOMPAT features this reader understands: FILETYPE, RECOVER (a mounted file system always has it),
// META_BG, EXTENTS, 64BIT, MMP, FLEX_BG, EA_INODE, CSUM_SEED, LARGEDIR, ENCRYPT, CASEFOLD.
// Not: COMPRESSION, JOURNAL_DEV (a journal, not a file system), DIRDATA (Lustre), INLINE_DATA.
constexpr uint32_t kKnownIncompat = 0x2 | 0x4 | 0x10 | 0x40 | 0x80 | 0x100 | 0x200 | 0x400 | 0x2000 | 0x4000 |
                                    0x10000 | 0x20000;
constexpr uint32_t kCompatSparseSuper2 = 0x200;
constexpr uint32_t kRoCompatBigalloc = 0x200;

bool is_dir_mode(uint16_t mode) { return (mode & 0xF000) == 0x4000; }
bool is_reg_mode(uint16_t mode) { return (mode & 0xF000) == 0x8000; }
bool is_link_mode(uint16_t mode) { return (mode & 0xF000) == 0xA000; }

int64_t to_ms(uint32_t seconds, uint32_t extra, bool has_extra) {
    // Seconds are signed 32-bit; the low two "extra" bits extend them past 2038 (fs/ext4/ext4.h).
    int64_t sec = static_cast<int32_t>(seconds);
    int64_t nsec = 0;
    if (has_extra) {
        sec += static_cast<int64_t>(extra & 3) << 32;
        nsec = std::min<int64_t>(extra >> 2, 999999999);
    }
    if (sec > kMaxUnixMs / 1000) return kMaxUnixMs;
    if (sec < kMinUnixMs / 1000) return kMinUnixMs;
    return clamp_unix_ms(sec * 1000 + nsec / 1000000);
}

bool is_power_of(uint32_t n, uint32_t base) {
    while (n > 1 && n % base == 0) n /= base;
    return n == 1;
}

bool group_has_super(const Superblock& sb, uint32_t group) {
    if (group == 0) return true;
    if (sb.feature_compat & kCompatSparseSuper2) return group == sb.backup_bgs[0] || group == sb.backup_bgs[1];
    if (group <= 1 || !sb.has_sparse_super()) return true;
    if (!(group & 1)) return false;
    return is_power_of(group, 3) || is_power_of(group, 5) || is_power_of(group, 7);
}

class BlockReader {
public:
    BlockReader(BlockSource& src, const Superblock& sb) : src_(src), sb_(sb), buf_(sb.block_size) {}
    const uint8_t* read(uint64_t block) {
        if (block >= sb_.block_count) return nullptr;
        if (!src_.read(block * sb_.block_size, buf_.data(), sb_.block_size)) return nullptr;
        return buf_.data();
    }

private:
    BlockSource& src_;
    const Superblock& sb_;
    std::vector<uint8_t> buf_;
};

// Extent tree walk. `budget` limits block reads so a damaged tree cannot make us read forever.
bool walk_extents(BlockSource& src, const Superblock& sb, const uint8_t* node, size_t length, int depth_limit,
                  const BlockLimits& limits, uint64_t& budget, std::vector<uint64_t>& out) {
    if (length < 12 || le16(node) != kExtentMagic) return false;
    const uint16_t entries = le16(node + 2);
    const uint16_t max_entries = le16(node + 4);
    const uint16_t depth = le16(node + 6);
    if (entries > max_entries || 12 + size_t{entries} * 12 > length || depth > depth_limit) return false;
    for (uint16_t i = 0; i < entries; ++i) {
        const uint8_t* e = node + 12 + size_t{i} * 12;
        if (depth == 0) {
            const uint64_t logical = le32(e);
            uint32_t len = le16(e + 4);
            const bool uninit = len > 32768;
            if (uninit) len -= 32768;
            const uint64_t start = (static_cast<uint64_t>(le16(e + 6)) << 32) | le32(e + 8);
            if (uninit || logical >= limits.max_logical) continue; // unwritten extents read back as zeros
            const uint64_t count = std::min<uint64_t>(len, limits.max_logical - logical);
            if (start >= sb.block_count || count > sb.block_count - start) return false;
            for (uint64_t k = 0; k < count; ++k) {
                if (out.size() >= limits.max_count) return false; // more mapped than allocated: damaged
                out.push_back(start + k);
            }
        } else {
            const uint64_t leaf = (static_cast<uint64_t>(le16(e + 8)) << 32) | le32(e + 4);
            if (budget == 0 || leaf >= sb.block_count) return false;
            --budget;
            std::vector<uint8_t> child(sb.block_size);
            if (!src.read(leaf * sb.block_size, child.data(), child.size())) return false;
            if (le16(child.data() + 6) != depth - 1) return false;
            if (!walk_extents(src, sb, child.data(), child.size(), depth - 1, limits, budget, out)) return false;
        }
    }
    return true;
}

// Classic ext2/ext3 block map: 12 direct pointers, then single, double and triple indirect blocks.
bool walk_indirect(BlockSource& src, const Superblock& sb, uint32_t pointer, int level, uint64_t& logical,
                   const BlockLimits& limits, std::vector<uint64_t>& out) {
    const uint64_t per_block = sb.block_size / 4;
    uint64_t span = 1;
    for (int i = 0; i < level; ++i) span *= per_block;
    if (pointer == 0) { // hole
        logical += span;
        return true;
    }
    if (pointer >= sb.block_count) return false;
    if (level == 0) {
        if (out.size() >= limits.max_count) return false; // more mapped than allocated: damaged
        out.push_back(pointer);
        ++logical;
        return true;
    }
    std::vector<uint8_t> block(sb.block_size);
    if (!src.read(uint64_t{pointer} * sb.block_size, block.data(), block.size())) return false;
    for (uint64_t i = 0; i < per_block && logical < limits.max_logical; ++i)
        if (!walk_indirect(src, sb, le32(block.data() + i * 4), level - 1, logical, limits, out)) return false;
    return true;
}

unsigned thread_count(unsigned requested) {
    if (requested != 0) return std::clamp(requested, 1u, 64u);
    return std::clamp(std::thread::hardware_concurrency(), 2u, 8u);
}

// Runs fn(i) for i in [0, count) on a few threads. fn must not throw; failures are reported through state.
template <class Fn>
void parallel_for(size_t count, unsigned threads, Fn&& fn) {
    std::atomic<size_t> next{0};
    auto work = [&] {
        for (size_t i; (i = next.fetch_add(1, std::memory_order_relaxed)) < count;) fn(i);
    };
    std::vector<std::thread> pool;
    const unsigned extra = static_cast<unsigned>(std::min<size_t>(threads, count)) - (count > 0 ? 1 : 0);
    try {
        for (unsigned t = 0; t < extra; ++t) pool.emplace_back(work);
    } catch (...) {
        // fewer threads, same result
    }
    work();
    for (auto& t : pool) t.join();
}

} // namespace

SuperblockCheck parse_superblock(const uint8_t* d, size_t length, Superblock& sb) {
    sb = Superblock{};
    if (length < kSuperblockSize || le16(d + 0x38) != kMagic) return SuperblockCheck::NotExt;
    const uint32_t log_block = le32(d + 0x18);
    if (log_block > 6) return SuperblockCheck::NotExt; // 1 KiB .. 64 KiB
    sb.block_size = 1024u << log_block;
    sb.inode_count = le32(d + 0x00);
    sb.block_count = le32(d + 0x04);
    sb.first_data_block = le32(d + 0x14);
    sb.blocks_per_group = le32(d + 0x20);
    sb.inodes_per_group = le32(d + 0x28);
    const uint32_t rev = le32(d + 0x4C);
    sb.inode_size = rev >= 1 ? le16(d + 0x58) : 128;
    sb.feature_compat = le32(d + 0x5C);
    sb.feature_incompat = le32(d + 0x60);
    sb.feature_ro_compat = le32(d + 0x64);
    std::memcpy(sb.uuid, d + 0x68, 16);
    sb.first_meta_bg = le32(d + 0x104);
    sb.backup_bgs[0] = le32(d + 0x24C);
    sb.backup_bgs[1] = le32(d + 0x250);
    if (sb.has_64bit()) {
        sb.block_count |= static_cast<uint64_t>(le32(d + 0x150)) << 32;
        sb.desc_size = le16(d + 0xFE);
    } else {
        sb.desc_size = 32;
    }

    // bigalloc allocates in clusters of blocks, which the block maps and sizes here do not account for.
    if (sb.feature_ro_compat & kRoCompatBigalloc) return SuperblockCheck::Unsupported;

    if (sb.inode_size < 128 || (sb.inode_size & (sb.inode_size - 1)) != 0 || sb.inode_size > sb.block_size)
        return SuperblockCheck::NotExt;
    if (sb.desc_size < 32 || sb.desc_size > 1024 || (sb.desc_size & (sb.desc_size - 1)) != 0 ||
        (sb.has_64bit() && sb.desc_size < 64))
        return SuperblockCheck::NotExt;
    if (sb.blocks_per_group == 0 || sb.blocks_per_group > 8ull * sb.block_size || sb.inodes_per_group == 0 ||
        sb.inodes_per_group > 8ull * sb.block_size || sb.block_count <= sb.first_data_block)
        return SuperblockCheck::NotExt;
    const uint64_t groups = (sb.block_count - sb.first_data_block + sb.blocks_per_group - 1) / sb.blocks_per_group;
    if (groups == 0 || groups > UINT32_MAX || uint64_t{sb.inodes_per_group} * groups != sb.inode_count)
        return SuperblockCheck::NotExt;
    // load() sizes its per-group arrays from this count before reading anything else, so a crafted superblock
    // (e.g. one inode per group) must not claim billions of groups. Past the cap the walker reads it instead.
    if (groups > kMaxGroups) return SuperblockCheck::Unsupported;
    sb.group_count = static_cast<uint32_t>(groups);

    if ((sb.feature_incompat & ~kKnownIncompat) != 0) return SuperblockCheck::Unsupported;
    return SuperblockCheck::Ok;
}

uint64_t descriptor_block(const Superblock& sb, uint32_t group) {
    const uint32_t per_block = sb.block_size / sb.desc_size;
    const uint32_t meta_group = group / per_block;
    // The superblock lives in block 1 with 1 KiB blocks, in block 0 otherwise; descriptors follow it.
    const uint64_t sb_block = sb.block_size == 1024 ? 1 : 0;
    if (!sb.has_meta_bg() || meta_group < sb.first_meta_bg) return sb_block + 1 + meta_group;
    // meta_bg: each run of `per_block` groups keeps its descriptors in its first group (fs/ext4/super.c).
    const uint64_t first_group = uint64_t{meta_group} * per_block;
    uint64_t has_super = group_has_super(sb, static_cast<uint32_t>(first_group)) ? 1 : 0;
    if (sb.block_size == 1024 && meta_group == 0 && sb.first_data_block == 0) ++has_super;
    return has_super + sb.first_data_block + first_group * sb.blocks_per_group;
}

bool parse_inode(const uint8_t* d, const Superblock& sb, Inode& out) {
    out = Inode{};
    out.mode = le16(d + 0x00);
    out.links = le16(d + 0x1A);
    if (out.mode == 0 || out.links == 0) return false; // free, or deleted
    out.flags = le32(d + 0x20);
    // The high size word counts only for regular files, or for folders with largedir (ext4_isize in fs/ext4/ext4.h);
    // on old ext2 folders that field was i_dir_acl.
    out.size = le32(d + 0x04);
    if (is_reg_mode(out.mode) || sb.has_largedir()) out.size |= static_cast<uint64_t>(le32(d + 0x6C)) << 32;
    uint64_t blocks = le32(d + 0x1C);
    if (sb.has_huge_file()) blocks |= static_cast<uint64_t>(le16(d + 0x74)) << 32;
    out.allocated = (sb.has_huge_file() && (out.flags & kFlagHugeFile)) ? blocks * sb.block_size : blocks * 512;

    uint32_t extra_isize = sb.inode_size > 128 ? le16(d + 0x80) : 0;
    if (128 + extra_isize > sb.inode_size) extra_isize = 0;
    const bool mtime_extra = extra_isize >= 0x0C; // i_mtime_extra at 0x88
    const bool atime_extra = extra_isize >= 0x10; // i_atime_extra at 0x8C
    out.mtime = to_ms(le32(d + 0x10), mtime_extra ? le32(d + 0x88) : 0, mtime_extra);
    out.atime = to_ms(le32(d + 0x08), atime_extra ? le32(d + 0x8C) : 0, atime_extra);
    std::memcpy(out.block, d + 0x28, sizeof(out.block));
    return true;
}

bool directory_blocks(BlockSource& src, const Superblock& sb, const Inode& dir, BlockLimits limits,
                      std::vector<uint64_t>& out) {
    out.clear();
    limits.max_logical = std::min(limits.max_logical, sb.block_count);
    limits.max_count = std::min(limits.max_count, sb.block_count);
    if (limits.max_logical == 0) return true;
    if (dir.flags & kFlagInlineData) return false;
    if (dir.flags & kFlagExtents) {
        uint64_t budget = limits.max_count + 8; // a valid tree has fewer index/leaf blocks than data blocks
        return walk_extents(src, sb, dir.block, sizeof(dir.block), kMaxExtentDepth, limits, budget, out);
    }
    uint64_t logical = 0;
    for (int i = 0; i < 15 && logical < limits.max_logical; ++i) {
        const int level = i < 12 ? 0 : i - 11;
        if (!walk_indirect(src, sb, le32(dir.block + i * 4), level, logical, limits, out)) return false;
    }
    return true;
}

es_status Ext4Index::load(BlockSource& src, const ReadOptions& options) {
    *this = Ext4Index{};
    const auto cancelled = [&] { return options.cancel && options.cancel->load(std::memory_order_relaxed); };

    uint8_t super[kSuperblockSize];
    if (!src.read(kSuperblockOffset, super, sizeof(super))) return ES_ERR_IO;
    if (parse_superblock(super, sizeof(super), sb_) != SuperblockCheck::Ok) return ES_ERR_NOT_SUPPORTED;
    const Superblock& sb = sb_;

    // Group descriptors.
    std::vector<GroupDesc> groups(sb.group_count);
    {
        BlockReader reader(src, sb);
        const uint32_t per_block = sb.block_size / sb.desc_size;
        uint64_t cached = UINT64_MAX;
        const uint8_t* block = nullptr;
        const uint64_t table_blocks = (uint64_t{sb.inodes_per_group} * sb.inode_size + sb.block_size - 1) / sb.block_size;
        for (uint32_t g = 0; g < sb.group_count; ++g) {
            const uint64_t where = descriptor_block(sb, g);
            if (where != cached) {
                block = reader.read(where);
                if (!block) return ES_ERR_IO;
                cached = where;
            }
            const uint8_t* d = block + size_t{g % per_block} * sb.desc_size;
            GroupDesc& gd = groups[g];
            gd.inode_bitmap = le32(d + 0x04);
            gd.inode_table = le32(d + 0x08);
            gd.flags = le16(d + 0x12);
            gd.itable_unused = le16(d + 0x1C);
            if (sb.has_64bit() && sb.desc_size >= 64) {
                gd.inode_bitmap |= static_cast<uint64_t>(le32(d + 0x24)) << 32;
                gd.inode_table |= static_cast<uint64_t>(le32(d + 0x28)) << 32;
                gd.itable_unused |= static_cast<uint32_t>(le16(d + 0x32)) << 16;
            }
            if (gd.inode_table == 0 || gd.inode_table >= sb.block_count || table_blocks > sb.block_count - gd.inode_table ||
                gd.inode_bitmap == 0 || gd.inode_bitmap >= sb.block_count)
                return ES_ERR_NOT_SUPPORTED; // not a file system we can make sense of
        }
    }

    const unsigned threads = thread_count(options.threads);
    std::atomic<int> failure{ES_OK};
    const auto fail = [&](es_status s) {
        int expected = ES_OK;
        failure.compare_exchange_strong(expected, s);
    };

    // Pass 1: every inode table, keeping the inodes in use (and the block maps of directories).
    struct DirInode {
        uint32_t ino;
        Inode inode;
    };
    std::vector<std::vector<Entry>> group_entries(sb.group_count);
    std::vector<std::vector<DirInode>> group_dirs(sb.group_count);
    parallel_for(sb.group_count, threads, [&](size_t g) {
        if (failure.load(std::memory_order_relaxed) != ES_OK) return;
        if (cancelled()) return fail(ES_ERR_CANCELLED);
        const GroupDesc& gd = groups[g];
        uint32_t used = sb.inodes_per_group;
        if (sb.has_group_checksums()) {
            if (gd.flags & kBgInodeUninit) used = 0;
            else used -= std::min(gd.itable_unused, used);
        }
        if (used == 0) return;
        try {
            // The inode bitmap says which table blocks hold inodes in use, so only those are read. ext2/ext3
            // (no group checksums, so no itable_unused hint) would otherwise mean reading every inode table.
            std::vector<uint8_t> bitmap(sb.block_size);
            if (!src.read(gd.inode_bitmap * sb.block_size, bitmap.data(), bitmap.size())) return fail(ES_ERR_IO);
            const auto in_use = [&](uint32_t i) { return (bitmap[i >> 3] >> (i & 7)) & 1; };
            const uint32_t per_block = sb.block_size / sb.inode_size;
            const uint32_t table_blocks = (used + per_block - 1) / per_block;
            std::vector<uint8_t> wanted(table_blocks, 0);
            for (uint32_t i = 0; i < used; ++i)
                if (in_use(i)) wanted[i / per_block] = 1;

            auto& entries = group_entries[g];
            auto& dirs = group_dirs[g];
            std::vector<uint8_t> table;
            Inode inode;
            uint64_t found = 0;
            for (uint32_t b = 0; b < table_blocks;) {
                if (!wanted[b]) {
                    ++b;
                    continue;
                }
                uint32_t e = b + 1;
                while (e < table_blocks && wanted[e]) ++e;
                table.resize(size_t{e - b} * sb.block_size);
                if (!src.read((gd.inode_table + b) * sb.block_size, table.data(), table.size())) return fail(ES_ERR_IO);
                const uint32_t last = std::min(used, e * per_block);
                for (uint32_t i = b * per_block; i < last; ++i) {
                    if (!parse_inode(table.data() + size_t{i - b * per_block} * sb.inode_size, sb, inode)) continue;
                    const uint32_t ino = static_cast<uint32_t>(g * sb.inodes_per_group + i + 1);
                    entries.push_back({ino, inode.mode, inode.links, inode.flags, inode.size, inode.allocated,
                                       inode.mtime, inode.atime});
                    if (is_dir_mode(inode.mode)) dirs.push_back({ino, inode});
                    ++found;
                }
                b = e;
            }
            if (options.inodes_read) options.inodes_read->fetch_add(found, std::memory_order_relaxed);
        } catch (const std::bad_alloc&) {
            fail(ES_ERR_NO_MEMORY);
        }
    });
    if (failure != ES_OK) return static_cast<es_status>(failure.load());

    size_t total = 0, total_dirs = 0;
    for (uint32_t g = 0; g < sb.group_count; ++g) {
        total += group_entries[g].size();
        total_dirs += group_dirs[g].size();
    }
    inodes_.reserve(total);
    group_start_.reserve(size_t{sb.group_count} + 1);
    std::vector<DirInode> dir_inodes;
    dir_inodes.reserve(total_dirs);
    for (uint32_t g = 0; g < sb.group_count; ++g) {
        group_start_.push_back(static_cast<uint32_t>(inodes_.size()));
        inodes_.insert(inodes_.end(), group_entries[g].begin(), group_entries[g].end());
        dir_inodes.insert(dir_inodes.end(), group_dirs[g].begin(), group_dirs[g].end());
        std::vector<Entry>().swap(group_entries[g]);
        std::vector<DirInode>().swap(group_dirs[g]);
    }
    group_start_.push_back(static_cast<uint32_t>(inodes_.size()));
    dirs_.reserve(dir_inodes.size());
    for (const auto& d : dir_inodes) dirs_.push_back(d.ino);

    // Pass 2a: where each directory's blocks are (reads extent index / indirect blocks).
    struct DirBlock {
        uint64_t physical;
        uint32_t dir; // index into dirs_
    };
    std::vector<std::vector<DirBlock>> per_dir(dir_inodes.size());
    std::vector<uint8_t> damaged(dir_inodes.size(), 0);
    // Directory blocks are distinct blocks of the file system, so there cannot be more than it has. Damaged or
    // crafted metadata that claims more is refused (the walker takes over) instead of exhausting memory.
    std::atomic<uint64_t> dir_block_total{0};
    const auto blocks_for = [&](uint64_t bytes) { return bytes / sb.block_size + (bytes % sb.block_size != 0); };
    parallel_for(dir_inodes.size(), threads, [&](size_t i) {
        if (failure.load(std::memory_order_relaxed) != ES_OK) return;
        if ((i & 0x3FF) == 0 && cancelled()) return fail(ES_ERR_CANCELLED);
        const Inode& inode = dir_inodes[i].inode;
        if (inode.flags & kFlagEncrypt) return; // names are ciphertext; build_tree flags the folder
        try {
            std::vector<uint64_t> blocks;
            // Its data blocks lie within its size, and there are no more of them than are allocated to it (i_blocks also
            // counts extent tree blocks, so it is an upper bound). Two limits: a folder may have holes.
            const BlockLimits limits{blocks_for(inode.size), blocks_for(inode.allocated)};
            if (!directory_blocks(src, sb, inode, limits, blocks)) damaged[i] = 1;
            if (dir_block_total.fetch_add(blocks.size(), std::memory_order_relaxed) + blocks.size() > sb.block_count)
                return fail(ES_ERR_NOT_SUPPORTED);
            auto& out = per_dir[i];
            out.reserve(blocks.size());
            for (uint64_t b : blocks) out.push_back({b, static_cast<uint32_t>(i)});
        } catch (const std::bad_alloc&) {
            fail(ES_ERR_NO_MEMORY);
        }
    });
    if (failure != ES_OK) return static_cast<es_status>(failure.load());
    std::vector<DirInode>().swap(dir_inodes);

    std::vector<DirBlock> all;
    {
        size_t n = 0;
        for (const auto& v : per_dir) n += v.size();
        all.reserve(n);
        for (auto& v : per_dir) {
            all.insert(all.end(), v.begin(), v.end());
            std::vector<DirBlock>().swap(v);
        }
    }
    std::sort(all.begin(), all.end(), [](const DirBlock& a, const DirBlock& b) {
        return a.physical != b.physical ? a.physical < b.physical : a.dir < b.dir;
    });
    // A block claimed by two folders is damage, or a block reused while we read: list it under one of them only.
    {
        size_t kept = 0;
        for (size_t i = 0; i < all.size(); ++i) {
            if (kept > 0 && all[kept - 1].physical == all[i].physical) {
                damaged[all[i].dir] = 1;
                continue;
            }
            all[kept++] = all[i];
        }
        all.resize(kept);
    }

    // Pass 2b: read directory blocks in disk order, contiguous blocks in one read, and parse the entries.
    constexpr size_t kRunBlocks = 256;
    struct Run {
        size_t first, count; // into `all`
    };
    std::vector<Run> runs;
    for (size_t i = 0; i < all.size();) {
        size_t j = i + 1;
        while (j < all.size() && j - i < kRunBlocks && all[j].physical == all[j - 1].physical + 1) ++j;
        runs.push_back({i, j - i});
        i = j;
    }
    struct Chunk {
        std::vector<Link> links;
        std::string names;
        std::vector<uint32_t> damaged;
    };
    std::vector<Chunk> chunks(runs.size());
    const bool filetype = sb.has_filetype();
    parallel_for(runs.size(), threads, [&](size_t r) {
        if (failure.load(std::memory_order_relaxed) != ES_OK) return;
        if ((r & 0xFF) == 0 && cancelled()) return fail(ES_ERR_CANCELLED);
        const Run run = runs[r];
        try {
            std::vector<uint8_t> data(run.count * sb.block_size);
            if (!src.read(all[run.first].physical * sb.block_size, data.data(), data.size())) return fail(ES_ERR_IO);
            Chunk& chunk = chunks[r];
            const auto parse = [&](const uint8_t* block, uint32_t dir) {
                return parse_dir_block(block, sb.block_size, filetype, [&](uint32_t child, std::string_view name) {
                    chunk.links.push_back({dir, child, static_cast<uint32_t>(chunk.names.size()),
                                           static_cast<uint16_t>(name.size())});
                    chunk.names.append(name);
                });
            };
            std::vector<uint8_t> again;
            for (size_t k = 0; k < run.count; ++k) {
                const uint32_t dir = all[run.first + k].dir;
                const size_t links_before = chunk.links.size(), names_before = chunk.names.size();
                if (parse(data.data() + k * sb.block_size, dir)) continue;
                // On a mounted file system a read can catch a block mid-update: read it once more. If it is still
                // malformed, keep the entries before the damage and flag the folder.
                chunk.links.resize(links_before);
                chunk.names.resize(names_before);
                again.resize(sb.block_size);
                const uint64_t physical = all[run.first + k].physical;
                const bool reread = src.read(physical * sb.block_size, again.data(), again.size());
                if (!parse(reread ? again.data() : data.data() + k * sb.block_size, dir)) chunk.damaged.push_back(dir);
            }
        } catch (const std::bad_alloc&) {
            fail(ES_ERR_NO_MEMORY);
        }
    });
    if (failure != ES_OK) return static_cast<es_status>(failure.load());
    std::vector<DirBlock>().swap(all);

    // Merge, then order the entries by directory (counting sort).
    size_t link_count = 0, name_bytes = 0;
    for (const auto& c : chunks) {
        link_count += c.links.size();
        name_bytes += c.names.size();
        for (uint32_t d : c.damaged) damaged[d] = 1;
    }
    if (name_bytes > UINT32_MAX || link_count > UINT32_MAX) return ES_ERR_NO_MEMORY;
    names_.reserve(name_bytes);
    dir_start_.assign(dirs_.size() + 1, 0);
    for (const auto& c : chunks)
        for (const auto& l : c.links) ++dir_start_[l.parent + 1];
    for (size_t d = 0; d < dirs_.size(); ++d) dir_start_[d + 1] += dir_start_[d];
    links_.resize(link_count);
    std::vector<uint32_t> fill(dir_start_.begin(), dir_start_.end() - 1);
    for (auto& c : chunks) {
        const auto base = static_cast<uint32_t>(names_.size());
        names_.append(c.names);
        for (Link l : c.links) {
            l.name_offset += base;
            links_[fill[l.parent]++] = l;
        }
        std::vector<Link>().swap(c.links);
        std::string().swap(c.names);
    }
    for (size_t d = 0; d < dirs_.size(); ++d)
        if (damaged[d]) damaged_dirs_.push_back(static_cast<uint32_t>(d));
    return cancelled() ? ES_ERR_CANCELLED : ES_OK;
}

const Ext4Index::Entry* Ext4Index::find(uint32_t ino) const {
    if (ino == 0 || ino > sb_.inode_count || group_start_.empty()) return nullptr;
    const uint32_t g = (ino - 1) / sb_.inodes_per_group;
    const auto first = inodes_.begin() + group_start_[g];
    const auto last = inodes_.begin() + group_start_[g + 1];
    const auto it = std::lower_bound(first, last, ino, [](const Entry& e, uint32_t v) { return e.ino < v; });
    return it != last && it->ino == ino ? &*it : nullptr;
}

uint32_t Ext4Index::dir_index(uint32_t ino) const {
    const auto it = std::lower_bound(dirs_.begin(), dirs_.end(), ino);
    return it != dirs_.end() && *it == ino ? static_cast<uint32_t>(it - dirs_.begin()) : UINT32_MAX;
}

uint32_t Ext4Index::find_child(uint32_t dir_ino, std::string_view name) const {
    const uint32_t d = dir_index(dir_ino);
    if (d == UINT32_MAX) return 0;
    const Entry* dir = find(dir_ino);
    const bool casefold = dir && (dir->flags & kFlagCasefold);
    const auto lower = [](char c) { return c >= 'A' && c <= 'Z' ? static_cast<char>(c - 'A' + 'a') : c; };
    const auto same = [&](std::string_view a) {
        if (a.size() != name.size()) return false;
        if (!casefold) return a == name;
        for (size_t i = 0; i < a.size(); ++i)
            if (lower(a[i]) != lower(name[i])) return false;
        return true;
    };
    for (uint32_t k = dir_start_[d]; k < dir_start_[d + 1]; ++k) {
        const Link& l = links_[k];
        if (same(std::string_view(names_.data() + l.name_offset, l.name_length))) return l.child;
    }
    return 0;
}

bool Ext4Index::build_tree(uint32_t root_ino, std::u16string_view root_name,
                           const std::vector<uint32_t>& skip_dirs, NodeStore& store,
                           const std::atomic<bool>& cancel, uint64_t& files, uint64_t& dirs, uint64_t& bytes,
                           uint32_t& errors) const {
    files = dirs = bytes = 0;
    errors = 0;
    const Entry* root = find(root_ino);
    const uint32_t root_dir = dir_index(root_ino);
    if (!root || !is_dir_mode(root->mode) || root_dir == UINT32_MAX) return false;

    const std::set<uint32_t> skipped(skip_dirs.begin(), skip_dirs.end());
    const auto is_damaged = [this](uint32_t d) {
        return std::binary_search(damaged_dirs_.begin(), damaged_dirs_.end(), d);
    };

    EntryInfo root_info;
    root_info.name = root_name;
    root_info.flags = ES_NODE_DIRECTORY;
    root_info.mtime = root->mtime;
    root_info.atime = root->atime;
    root_info.attributes = root->mode;
    store.add_root(root_info);
    if (root->flags & kFlagEncrypt) { // names are ciphertext
        store.mark_error(0);
        ++errors;
        return true;
    }
    if (is_damaged(root_dir)) { // partly readable: list what can be read
        store.mark_error(0);
        ++errors;
    }

    std::vector<uint8_t> visited(dirs_.size(), 0);
    std::vector<uint8_t> counted(inodes_.size(), 0); // hard-linked files already counted in this subtree
    visited[root_dir] = 1;

    struct Pending {
        uint32_t dir; // index into dirs_
        uint32_t node;
    };
    std::vector<Pending> queue{{root_dir, 0}};
    std::vector<EntryInfo> batch;
    std::vector<std::u16string> batch_names;
    std::vector<uint32_t> batch_dir;
    for (size_t q = 0; q < queue.size(); ++q) {
        if ((q & 0xFFF) == 0 && cancel.load(std::memory_order_relaxed)) return false;
        const auto [dir, dir_node] = queue[q];
        batch.clear();
        batch_names.clear();
        batch_dir.clear();
        for (uint32_t k = dir_start_[dir]; k < dir_start_[dir + 1]; ++k) {
            const Link& l = links_[k];
            const Entry* e = find(l.child);
            if (!e) continue; // entry for an inode that is gone (changed while reading)
            const std::string_view raw(names_.data() + l.name_offset, l.name_length);
            EntryInfo info;
            info.mtime = e->mtime;
            info.atime = e->atime;
            info.attributes = e->mode;
            uint32_t descend = UINT32_MAX;
            if (is_dir_mode(e->mode)) {
                info.flags = ES_NODE_DIRECTORY;
                const uint32_t d = dir_index(l.child);
                if (d == UINT32_MAX || (e->flags & kFlagEncrypt)) {
                    info.flags |= ES_NODE_ERROR;
                    ++errors;
                } else if (visited[d] || skipped.contains(l.child)) {
                    info.flags |= ES_NODE_REPARSE; // a mount point, or a folder already listed elsewhere
                } else {
                    if (is_damaged(d)) { // some of its entries could not be read; list the rest
                        info.flags |= ES_NODE_ERROR;
                        ++errors;
                    }
                    visited[d] = 1;
                    descend = d;
                }
                ++dirs;
            } else {
                ++files;
                const auto idx = static_cast<size_t>(e - inodes_.data());
                if (is_reg_mode(e->mode) && e->links > 1 && counted[idx]) {
                    info.flags = ES_NODE_HARDLINK;
                } else {
                    if (is_reg_mode(e->mode) && e->links > 1) {
                        counted[idx] = 1;
                        info.flags = ES_NODE_HAS_LINKS;
                    }
                    if (is_link_mode(e->mode)) info.flags = ES_NODE_REPARSE;
                    info.size = e->size;
                    info.allocated = e->allocated;
                    bytes += info.size;
                }
            }
            batch_names.push_back(utf8_to_utf16(raw));
            batch.push_back(info);
            batch_dir.push_back(descend);
        }
        for (size_t i = 0; i < batch.size(); ++i) batch[i].name = batch_names[i];
        const uint32_t first = store.append_children(dir_node, batch.data(), batch.size());
        for (size_t i = 0; i < batch.size(); ++i)
            if (batch_dir[i] != UINT32_MAX) queue.push_back({batch_dir[i], first + static_cast<uint32_t>(i)});
    }
    return true;
}

} // namespace emmascan::ext4
