// emmascan-bench: times scans through the public C API, for development only (never packaged).
//
//     emmascan-bench <folder> [--mode auto|walker|mft|ext4] [--threads N] [--repeat N] [--dump FILE]
//
// Prints one line per run (time, mode used, counts, totals, errors). --dump writes every node of the last run
// as "path<TAB>size<TAB>allocated<TAB>flags", sorted by path, so two scan methods can be compared with diff.
// On macOS, EMMASCAN_NO_BULK=1 makes the engine list folders with readdir + fstatat instead of getattrlistbulk.

#include "emmascan/emmascan.h"
#include "utf8.h"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <string_view>
#include <vector>

#if defined(_WIN32)
#include <cwchar>
#endif

namespace {

struct Options {
    std::u16string root;
    uint32_t mode = ES_MODE_AUTO;
    uint32_t threads = 0;
    int repeat = 1;
    const char* dump = nullptr;
};

const char* mode_name(uint32_t mode) {
    switch (mode) {
    case ES_MODE_WALKER: return "walker";
    case ES_MODE_MFT: return "mft";
    case ES_MODE_EXT4: return "ext4";
    default: return "auto";
    }
}

bool parse_mode(std::string_view s, uint32_t& mode) {
    if (s == "auto") mode = ES_MODE_AUTO;
    else if (s == "walker") mode = ES_MODE_WALKER;
    else if (s == "mft") mode = ES_MODE_MFT;
    else if (s == "ext4") mode = ES_MODE_EXT4;
    else return false;
    return true;
}

int usage() {
    std::fputs("usage: emmascan-bench <folder> [--mode auto|walker|mft|ext4] [--threads N] [--repeat N] [--dump FILE]\n",
               stderr);
    return 2;
}

std::string full_path(const es_result& r, uint32_t node) {
    std::vector<uint32_t> chain;
    for (uint32_t n = node; n != ES_NONE; n = r.nodes[n].parent) chain.push_back(n);
    std::string path;
    for (auto it = chain.rbegin(); it != chain.rend(); ++it) {
        const es_node& n = r.nodes[*it];
        const std::u16string_view name(reinterpret_cast<const char16_t*>(r.names) + n.name_offset, n.name_length);
        if (!path.empty() && path.back() != '/' && path.back() != '\\') path.push_back('/');
        path += emmascan::utf16_to_utf8(name);
    }
    return path;
}

bool dump(const es_result& r, const char* file) {
    std::vector<std::string> lines;
    lines.reserve(r.node_count);
    for (uint32_t i = 0; i < r.node_count; ++i) {
        const es_node& n = r.nodes[i];
        char tail[96];
        std::snprintf(tail, sizeof tail, "\t%llu\t%llu\t0x%02x", static_cast<unsigned long long>(n.size),
                      static_cast<unsigned long long>(n.allocated), static_cast<unsigned>(n.flags));
        lines.push_back(full_path(r, i) + tail);
    }
    std::sort(lines.begin(), lines.end());
    FILE* out = std::fopen(file, "wb");
    if (!out) return false;
    for (const auto& line : lines) {
        std::fwrite(line.data(), 1, line.size(), out);
        std::fputc('\n', out);
    }
    return std::fclose(out) == 0;
}

int run(const Options& o) {
    for (int i = 0; i < o.repeat; ++i) {
        es_scan_options options{};
        options.struct_size = sizeof(options);
        options.mode = o.mode;
        options.thread_count = o.threads;
        options.root_path = reinterpret_cast<const es_char*>(o.root.c_str());
        es_scan* scan = nullptr;
        es_status status = es_scan_start(&options, &scan);
        if (status == ES_OK) status = es_scan_wait(scan, 0xFFFFFFFFu);
        es_result r{};
        if (status == ES_OK) status = es_scan_get_result(scan, &r);
        if (status != ES_OK) {
            std::fprintf(stderr, "scan failed: status %d\n", static_cast<int>(status));
            es_scan_free(scan);
            return 1;
        }
        const es_node& root = r.nodes[0];
        std::printf("run %d: %.3f s  mode=%s  files=%u  folders=%u  bytes=%llu  allocated=%llu  errors=%u  nodes=%u\n",
                    i + 1, static_cast<double>(r.elapsed_ms) / 1000.0, mode_name(r.mode), root.file_count,
                    root.dir_count, static_cast<unsigned long long>(root.size),
                    static_cast<unsigned long long>(root.allocated), r.errors, r.node_count);
        std::fflush(stdout);
        if (o.dump && i + 1 == o.repeat && !dump(r, o.dump)) {
            std::fprintf(stderr, "could not write %s\n", o.dump);
            es_scan_free(scan);
            return 1;
        }
        es_scan_free(scan);
    }
    return 0;
}

} // namespace

#if defined(_WIN32)
int wmain(int argc, wchar_t** argv) {
    auto arg = [&](int i) {
        std::string s;
        for (const wchar_t* p = argv[i]; *p; ++p) s.push_back(static_cast<char>(*p < 0x80 ? *p : '?'));
        return s;
    };
    auto root = [&](int i) { return std::u16string(argv[i], argv[i] + std::wcslen(argv[i])); };
#else
int main(int argc, char** argv) {
    auto arg = [&](int i) { return std::string(argv[i]); };
    auto root = [&](int i) { return emmascan::utf8_to_utf16(argv[i]); };
#endif
    Options o;
    std::string dump_file;
    for (int i = 1; i < argc; ++i) {
        const std::string a = arg(i);
        const bool has_value = i + 1 < argc;
        if (a == "--mode" && has_value) {
            if (!parse_mode(arg(++i), o.mode)) return usage();
        } else if (a == "--threads" && has_value) {
            o.threads = static_cast<uint32_t>(std::strtoul(arg(++i).c_str(), nullptr, 10));
        } else if (a == "--repeat" && has_value) {
            o.repeat = std::max(1, std::atoi(arg(++i).c_str()));
        } else if (a == "--dump" && has_value) {
            dump_file = arg(++i);
        } else if (a.starts_with("--") || !o.root.empty()) {
            return usage();
        } else {
            o.root = root(i);
        }
    }
    if (o.root.empty()) return usage();
    if (!dump_file.empty()) o.dump = dump_file.c_str();
    return run(o);
}
