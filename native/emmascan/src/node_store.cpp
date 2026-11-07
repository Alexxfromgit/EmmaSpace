#include "node_store.h"

#include <algorithm>

namespace emmascan {

es_node NodeStore::make_node(const EntryInfo& e, uint32_t parent) {
    es_node n{};
    const bool is_dir = (e.flags & ES_NODE_DIRECTORY) != 0;
    n.size = is_dir ? 0 : e.size;
    n.allocated = is_dir ? 0 : e.allocated;
    n.mtime = e.mtime;
    n.atime = e.atime;
    n.parent = parent;
    n.first_child = ES_NONE;
    n.next_sibling = ES_NONE;
    n.name_offset = static_cast<uint32_t>(names_.size());
    n.name_length = static_cast<uint16_t>(std::min<size_t>(e.name.size(), UINT16_MAX));
    n.file_count = is_dir ? 0 : 1;
    n.dir_count = 0;
    n.attributes = e.attributes;
    n.flags = e.flags;
    names_.insert(names_.end(), e.name.begin(), e.name.begin() + n.name_length);
    return n;
}

uint32_t NodeStore::add_root(const EntryInfo& root) {
    nodes_.clear();
    names_.clear();
    nodes_.push_back(make_node(root, ES_NONE));
    return 0;
}

uint32_t NodeStore::append_children(uint32_t parent, const EntryInfo* entries, size_t count) {
    if (count == 0) return ES_NONE;
    const auto first = static_cast<uint32_t>(nodes_.size());
    for (size_t i = 0; i < count; ++i) {
        es_node n = make_node(entries[i], parent);
        n.next_sibling = (i + 1 < count) ? first + static_cast<uint32_t>(i) + 1 : nodes_[parent].first_child;
        nodes_.push_back(n);
    }
    nodes_[parent].first_child = first;
    return first;
}

void NodeStore::finalize() {
    for (size_t i = nodes_.size(); i-- > 1;) {
        const es_node& child = nodes_[i];
        es_node& parent = nodes_[child.parent];
        parent.size += child.size;
        parent.allocated += child.allocated;
        parent.file_count += child.file_count;
        parent.dir_count += child.dir_count + ((child.flags & ES_NODE_DIRECTORY) ? 1u : 0u);
    }

    std::vector<uint32_t> children;
    for (es_node& n : nodes_) {
        if (n.first_child == ES_NONE || nodes_[n.first_child].next_sibling == ES_NONE) continue;
        children.clear();
        for (uint32_t c = n.first_child; c != ES_NONE; c = nodes_[c].next_sibling) children.push_back(c);
        std::stable_sort(children.begin(), children.end(), [this](uint32_t a, uint32_t b) {
            return nodes_[a].size > nodes_[b].size;
        });
        n.first_child = children.front();
        for (size_t i = 0; i < children.size(); ++i)
            nodes_[children[i]].next_sibling = (i + 1 < children.size()) ? children[i + 1] : ES_NONE;
    }
}

} // namespace emmascan
