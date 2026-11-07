#include "mft_parser.h"

#include <algorithm>
#include <cstring>

namespace emmascan::ntfs {
namespace {

template <typename T>
T read(const uint8_t* p) {
    T v;
    std::memcpy(&v, p, sizeof(T));
    return v;
}

int64_t filetime_to_unix_ms(int64_t ft) {
    constexpr int64_t epoch_diff = 116444736000000000LL;
    return ft <= epoch_diff ? 0 : (ft - epoch_diff) / 10000;
}

} // namespace

bool decode_runlist(const uint8_t* p, const uint8_t* end, std::vector<DataRun>& out) {
    out.clear();
    int64_t lcn = 0;
    while (p < end && *p != 0) {
        const unsigned length_size = *p & 0x0F;
        const unsigned offset_size = *p >> 4;
        ++p;
        if (length_size == 0 || length_size > 8 || offset_size > 8 || p + length_size + offset_size > end) return false;

        uint64_t length = 0;
        for (unsigned i = 0; i < length_size; ++i) length |= static_cast<uint64_t>(p[i]) << (8 * i);
        p += length_size;

        if (offset_size == 0) {
            out.push_back({-1, length}); // sparse
            continue;
        }
        int64_t delta = 0;
        for (unsigned i = 0; i < offset_size; ++i) delta |= static_cast<int64_t>(p[i]) << (8 * i);
        if (offset_size < 8 && (p[offset_size - 1] & 0x80))
            delta |= ~((int64_t{1} << (8 * offset_size)) - 1); // sign-extend: offsets are relative and may be negative
        p += offset_size;
        lcn += delta;
        out.push_back({lcn, length});
    }
    return true;
}

bool apply_fixups(uint8_t* record, size_t record_size, size_t stride) {
    if (record_size < 48) return false;
    const auto usa_offset = read<uint16_t>(record + 4);
    const auto usa_count = read<uint16_t>(record + 6);
    if (usa_count < 2 || usa_offset + size_t{usa_count} * 2 > record_size || (usa_count - 1) * stride > record_size)
        return false;
    const auto usn = read<uint16_t>(record + usa_offset);
    for (size_t i = 1; i < usa_count; ++i) {
        uint8_t* tail = record + i * stride - 2;
        if (read<uint16_t>(tail) != usn) return false; // torn write
        std::memcpy(tail, record + usa_offset + i * 2, 2);
    }
    return true;
}

bool parse_record(const uint8_t* r, size_t size, ParsedRecord& out, bool want_runs) {
    out = ParsedRecord{};
    if (size < 48 || std::memcmp(r, "FILE", 4) != 0) return false;

    const auto flags = read<uint16_t>(r + 0x16);
    out.in_use = (flags & 0x01) != 0;
    out.is_directory = (flags & 0x02) != 0;
    out.sequence = read<uint16_t>(r + 0x10);
    out.base_ref = read<uint64_t>(r + 0x20) & kFrnMask;

    const size_t limit = std::min<size_t>(size, read<uint32_t>(r + 0x18));
    size_t offset = read<uint16_t>(r + 0x14);
    while (offset + 16 <= limit) {
        const uint8_t* a = r + offset;
        const auto type = read<uint32_t>(a);
        if (type == kAttrEnd) break;
        const auto length = read<uint32_t>(a + 4);
        if (length < 16 || offset + length > limit) break;
        const bool non_resident = a[8] != 0;
        const uint8_t name_length = a[9];

        const uint8_t* value = nullptr;
        uint32_t value_length = 0;
        if (!non_resident && length >= 0x18) {
            value_length = read<uint32_t>(a + 0x10);
            const auto value_offset = read<uint16_t>(a + 0x14);
            if (value_offset + size_t{value_length} <= length) value = a + value_offset;
        }

        switch (type) {
        case kAttrStandardInformation:
            if (value && value_length >= 36) {
                out.has_standard_info = true;
                out.mtime = filetime_to_unix_ms(read<int64_t>(value + 8));
                out.atime = filetime_to_unix_ms(read<int64_t>(value + 24));
                out.attributes = read<uint32_t>(value + 32);
            }
            break;
        case kAttrFileName:
            if (value && value_length >= 0x42) {
                const uint8_t chars = value[0x40];
                const uint8_t name_space = value[0x41];
                if (name_space != kNamespaceDos && 0x42 + size_t{chars} * 2 <= value_length) {
                    out.names.push_back({read<uint64_t>(value),
                                         {reinterpret_cast<const char16_t*>(value + 0x42), chars}});
                }
            }
            break;
        case kAttrData:
            if (name_length != 0) break; // alternate data stream
            if (!non_resident) {
                if (value) {
                    out.has_data = true;
                    out.data_size = value_length; // lives inside the MFT record itself
                    out.allocated = 0;
                }
            } else if (length >= 0x40 && read<uint64_t>(a + 0x10) == 0) { // first extent
                const auto attr_flags = read<uint16_t>(a + 0x0C);
                out.has_data = true;
                out.allocated = read<uint64_t>(a + 0x28);
                out.data_size = read<uint64_t>(a + 0x30);
                if ((attr_flags & 0x8001) && length >= 0x48) out.allocated = read<uint64_t>(a + 0x40); // compressed/sparse
                if (want_runs) {
                    const auto runs = read<uint16_t>(a + 0x20);
                    if (runs < length && !decode_runlist(a + runs, a + length, out.data_runs)) out.data_runs.clear();
                }
            }
            break;
        case kAttrReparsePoint:
            if (value && value_length >= 4) out.reparse_tag = read<uint32_t>(value);
            break;
        default:
            break;
        }
        offset += length;
    }
    return true;
}

MftIndex::MftIndex(uint64_t record_count) : entries_(record_count) {}

void MftIndex::add(uint32_t frn, const ParsedRecord& r) {
    if (!r.in_use || frn >= entries_.size()) return;
    const uint64_t target = r.base_ref != 0 ? r.base_ref : frn;
    if (target >= entries_.size()) return;
    Entry& e = entries_[target];
    if (r.base_ref == 0) {
        e.flags |= kInUse;
        if (r.is_directory) e.flags |= kDirectory;
        e.sequence = r.sequence;
    }
    if (r.has_standard_info) {
        e.flags |= kHasInfo;
        e.mtime = r.mtime;
        e.atime = r.atime;
        e.attributes = r.attributes;
    }
    if (r.has_data) {
        e.size = r.data_size;
        e.allocated = r.allocated;
    }
    if (r.reparse_tag) e.reparse_tag = r.reparse_tag;
    for (const auto& name : r.names) add_name(static_cast<uint32_t>(target), name);
}

void MftIndex::add_name(uint32_t frn, const FileNameRef& name) {
    const auto offset = static_cast<uint32_t>(names_.size());
    names_.insert(names_.end(), name.name.begin(), name.name.end());
    const uint64_t parent = name.parent_ref & kFrnMask;
    const auto parent_sequence = static_cast<uint16_t>(name.parent_ref >> 48);
    Entry& e = entries_[frn];
    if (!(e.flags & kHasName)) {
        e.flags |= kHasName;
        e.parent_frn = parent;
        e.parent_sequence = parent_sequence;
        e.name_offset = offset;
        e.name_length = static_cast<uint16_t>(name.name.size());
        return;
    }
    if (parent == e.parent_frn && name.name == name_at(e.name_offset, e.name_length)) return; // same link twice
    links_.push_back({frn, parent, parent_sequence, offset, static_cast<uint16_t>(name.name.size())});
}

bool MftIndex::valid_parent(uint64_t parent, uint16_t parent_sequence, uint32_t child) const {
    if (parent >= entries_.size() || parent == child) return false;
    const Entry& p = entries_[parent];
    return (p.flags & (kInUse | kDirectory)) == (kInUse | kDirectory) &&
           (parent_sequence == 0 || p.sequence == 0 || p.sequence == parent_sequence);
}

bool MftIndex::build_tree(uint32_t root_frn, std::u16string_view root_name, NodeStore& store,
                          const std::atomic<bool>& cancel, uint64_t& files, uint64_t& dirs, uint64_t& bytes) const {
    files = dirs = bytes = 0;
    const auto n = static_cast<uint32_t>(entries_.size());
    if (root_frn >= n || (entries_[root_frn].flags & (kInUse | kDirectory)) != (kInUse | kDirectory)) return false;

    // Children per parent via counting sort. Values >= n refer to links_[value - n].
    std::vector<uint32_t> start(size_t{n} + 1, 0);
    for (uint32_t i = 0; i < n; ++i) {
        const Entry& e = entries_[i];
        if ((e.flags & (kInUse | kHasName)) == (kInUse | kHasName) && valid_parent(e.parent_frn, e.parent_sequence, i))
            ++start[e.parent_frn + 1];
    }
    for (const auto& link : links_)
        if (!(entries_[link.frn].flags & kDirectory) && valid_parent(link.parent_frn, link.parent_sequence, link.frn))
            ++start[link.parent_frn + 1];
    for (uint32_t i = 0; i < n; ++i) start[i + 1] += start[i];

    std::vector<uint32_t> children(start[n]);
    std::vector<uint32_t> fill(start.begin(), start.end() - 1);
    for (uint32_t i = 0; i < n; ++i) {
        const Entry& e = entries_[i];
        if ((e.flags & (kInUse | kHasName)) == (kInUse | kHasName) && valid_parent(e.parent_frn, e.parent_sequence, i))
            children[fill[e.parent_frn]++] = i;
    }
    for (uint32_t l = 0; l < links_.size(); ++l) {
        const auto& link = links_[l];
        if (!(entries_[link.frn].flags & kDirectory) && valid_parent(link.parent_frn, link.parent_sequence, link.frn))
            children[fill[link.parent_frn]++] = n + l;
    }

    const Entry& root = entries_[root_frn];
    EntryInfo root_info;
    root_info.name = root_name;
    root_info.flags = ES_NODE_DIRECTORY;
    root_info.mtime = root.mtime;
    root_info.atime = root.atime;
    root_info.attributes = root.attributes;
    store.add_root(root_info);

    struct Pending {
        uint32_t frn;
        uint32_t node;
    };
    std::vector<Pending> queue{{root_frn, 0}};
    std::vector<EntryInfo> batch;
    std::vector<uint32_t> batch_frn;
    for (size_t q = 0; q < queue.size(); ++q) {
        if ((q & 0xFFF) == 0 && cancel.load(std::memory_order_relaxed)) return false;
        const auto [dir_frn, dir_node] = queue[q];
        batch.clear();
        batch_frn.clear();
        for (uint32_t k = start[dir_frn]; k < start[dir_frn + 1]; ++k) {
            const uint32_t c = children[k];
            EntryInfo info;
            if (c >= n) {
                const auto& link = links_[c - n];
                const Entry& target = entries_[link.frn];
                info.name = name_at(link.name_offset, link.name_length);
                info.flags = ES_NODE_HARDLINK;
                info.mtime = target.mtime;
                info.atime = target.atime;
                info.attributes = target.attributes;
                ++files;
                batch_frn.push_back(UINT32_MAX);
            } else {
                const Entry& e = entries_[c];
                info.name = name_at(e.name_offset, e.name_length);
                info.mtime = e.mtime;
                info.atime = e.atime;
                info.attributes = e.attributes;
                if (e.flags & kDirectory) {
                    info.flags = ES_NODE_DIRECTORY;
                    if ((e.attributes & kFileAttributeReparsePoint) && is_link_reparse_tag(e.reparse_tag))
                        info.flags |= ES_NODE_REPARSE;
                    ++dirs;
                } else {
                    info.size = e.size;
                    info.allocated = e.allocated;
                    if (e.attributes & kCloudAttributes) {
                        info.size = std::min(info.size, info.allocated); // online-only data takes no local space
                        info.flags |= ES_NODE_CLOUD;
                    }
                    bytes += info.size;
                    ++files;
                }
                batch_frn.push_back(c);
            }
            batch.push_back(info);
        }

        const uint32_t first = store.append_children(dir_node, batch.data(), batch.size());
        for (size_t i = 0; i < batch.size(); ++i) {
            if ((batch[i].flags & ES_NODE_DIRECTORY) && !(batch[i].flags & ES_NODE_REPARSE))
                queue.push_back({batch_frn[i], first + static_cast<uint32_t>(i)});
        }
    }
    return true;
}

} // namespace emmascan::ntfs
