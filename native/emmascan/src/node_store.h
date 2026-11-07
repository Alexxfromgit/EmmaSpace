#pragma once

#include "emmascan/emmascan.h"

#include <cstddef>
#include <cstdint>
#include <string_view>
#include <vector>

namespace emmascan {

// One directory entry as reported by a platform enumerator, before it becomes a node.
struct EntryInfo {
    std::u16string_view name;
    uint64_t size = 0;
    uint64_t allocated = 0;
    int64_t mtime = 0;
    int64_t atime = 0;
    uint32_t attributes = 0;
    uint16_t flags = 0;
};

// Builds the flat tree described in emmascan.h. Not thread-safe: callers serialize access.
class NodeStore {
public:
    uint32_t add_root(const EntryInfo& root);

    // Appends the children of `parent` and links them in. Returns the index of the first
    // appended child (children are contiguous), or ES_NONE when count is 0.
    uint32_t append_children(uint32_t parent, const EntryInfo* entries, size_t count);

    void mark_error(uint32_t node) { nodes_[node].flags |= ES_NODE_ERROR; }

    // Rolls sizes and counts up to the root and orders siblings by size, largest first.
    // Relies on children always having a larger index than their parent.
    void finalize();

    std::vector<es_node>& nodes() { return nodes_; }
    std::vector<char16_t>& names() { return names_; }
    const std::vector<es_node>& nodes() const { return nodes_; }
    const std::vector<char16_t>& names() const { return names_; }

    std::u16string_view name_of(uint32_t node) const {
        const es_node& n = nodes_[node];
        return {names_.data() + n.name_offset, n.name_length};
    }

private:
    es_node make_node(const EntryInfo& e, uint32_t parent);

    std::vector<es_node> nodes_;
    std::vector<char16_t> names_;
};

} // namespace emmascan
