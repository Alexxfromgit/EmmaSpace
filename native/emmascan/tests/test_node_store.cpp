#include "node_store.h"

#include <catch2/catch_test_macros.hpp>

#include <vector>

using emmascan::EntryInfo;
using emmascan::NodeStore;

namespace {

EntryInfo dir(std::u16string_view name) {
    EntryInfo e;
    e.name = name;
    e.flags = ES_NODE_DIRECTORY;
    return e;
}

EntryInfo file(std::u16string_view name, uint64_t size) {
    EntryInfo e;
    e.name = name;
    e.size = size;
    e.allocated = (size + 4095) / 4096 * 4096;
    return e;
}

std::vector<std::u16string_view> child_names(const NodeStore& store, uint32_t node) {
    std::vector<std::u16string_view> names;
    for (uint32_t c = store.nodes()[node].first_child; c != ES_NONE; c = store.nodes()[c].next_sibling)
        names.push_back(store.name_of(c));
    return names;
}

} // namespace

TEST_CASE("finalize rolls sizes and counts up to the root") {
    NodeStore store;
    store.add_root(dir(u"root"));
    const EntryInfo top[] = {dir(u"a"), file(u"x.bin", 100), dir(u"b")};
    const uint32_t first = store.append_children(0, top, 3);
    const EntryInfo in_a[] = {file(u"a1", 10), file(u"a2", 20)};
    store.append_children(first, in_a, 2);
    const EntryInfo in_b[] = {file(u"b1", 1000)};
    store.append_children(first + 2, in_b, 1);

    store.finalize();
    const auto& root = store.nodes()[0];
    CHECK(root.size == 1130);
    CHECK(root.file_count == 4);
    CHECK(root.dir_count == 2);
    CHECK(root.allocated == 4096 * 4);
    CHECK(store.nodes()[first].size == 30);
    CHECK(store.nodes()[first].file_count == 2);
}

TEST_CASE("finalize orders siblings largest first") {
    NodeStore store;
    store.add_root(dir(u"root"));
    const EntryInfo children[] = {file(u"small", 1), file(u"big", 300), file(u"mid", 20)};
    store.append_children(0, children, 3);
    store.finalize();

    const auto names = child_names(store, 0);
    REQUIRE(names.size() == 3);
    CHECK(names[0] == u"big");
    CHECK(names[1] == u"mid");
    CHECK(names[2] == u"small");
    CHECK(store.nodes()[store.nodes()[0].first_child].parent == 0);
}

TEST_CASE("directories have no own size and empty names are kept") {
    NodeStore store;
    store.add_root(dir(u"C:\\"));
    EntryInfo d = dir(u"empty");
    d.size = 999; // ignored for directories
    store.append_children(0, &d, 1);
    store.finalize();
    CHECK(store.nodes()[0].size == 0);
    CHECK(store.nodes()[1].dir_count == 0);
    CHECK(store.name_of(0) == u"C:\\");
}
