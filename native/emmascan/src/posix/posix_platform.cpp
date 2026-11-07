// macOS / Linux backend: a parallel directory walker on openat/fdopendir/fstatat.
//
// - Sizes: st_size is the logical size, st_blocks * 512 what is allocated on disk.
// - Hard links: files with st_nlink > 1 are counted once (later links get ES_NODE_HARDLINK, size 0).
// - Directories reached twice (macOS firmlinks such as /Users and /System/Volumes/Data/Users
//   share the same device and inode) are listed once; later sightings are flagged as links.
// - Symbolic links are recorded but never followed.
// - When scanning "/", pseudo file systems and other mounted volumes are skipped.

#include "../scan_state.h"

#include <dirent.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

#include <algorithm>
#include <cerrno>
#include <condition_variable>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_set>
#include <utility>
#include <vector>

namespace emmascan::platform {
namespace {

std::u16string utf8_to_utf16(std::string_view s) {
    std::u16string out;
    out.reserve(s.size());
    for (size_t i = 0; i < s.size();) {
        const auto c = static_cast<unsigned char>(s[i]);
        uint32_t cp;
        size_t n;
        if (c < 0x80) { cp = c; n = 1; }
        else if ((c >> 5) == 0x6) { cp = c & 0x1F; n = 2; }
        else if ((c >> 4) == 0xE) { cp = c & 0x0F; n = 3; }
        else if ((c >> 3) == 0x1E) { cp = c & 0x07; n = 4; }
        else { out.push_back(u'�'); ++i; continue; }
        if (i + n > s.size()) { out.push_back(u'�'); break; }
        bool ok = true;
        for (size_t k = 1; k < n; ++k) {
            const auto cc = static_cast<unsigned char>(s[i + k]);
            if ((cc >> 6) != 0x2) { ok = false; break; }
            cp = (cp << 6) | (cc & 0x3F);
        }
        if (!ok) { out.push_back(u'�'); ++i; continue; }
        if (cp >= 0x10000) {
            cp -= 0x10000;
            out.push_back(static_cast<char16_t>(0xD800 + (cp >> 10)));
            out.push_back(static_cast<char16_t>(0xDC00 + (cp & 0x3FF)));
        } else {
            out.push_back(static_cast<char16_t>(cp));
        }
        i += n;
    }
    return out;
}

std::string utf16_to_utf8(std::u16string_view s) {
    std::string out;
    out.reserve(s.size());
    for (size_t i = 0; i < s.size(); ++i) {
        uint32_t cp = s[i];
        if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < s.size() && s[i + 1] >= 0xDC00 && s[i + 1] <= 0xDFFF) {
            cp = 0x10000 + ((cp - 0xD800) << 10) + (s[i + 1] - 0xDC00);
            ++i;
        }
        if (cp < 0x80) {
            out.push_back(static_cast<char>(cp));
        } else if (cp < 0x800) {
            out.push_back(static_cast<char>(0xC0 | (cp >> 6)));
            out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        } else if (cp < 0x10000) {
            out.push_back(static_cast<char>(0xE0 | (cp >> 12)));
            out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        } else {
            out.push_back(static_cast<char>(0xF0 | (cp >> 18)));
            out.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        }
    }
    return out;
}

int64_t to_unix_ms(const struct timespec& t) { return static_cast<int64_t>(t.tv_sec) * 1000 + t.tv_nsec / 1000000; }

#if defined(__APPLE__)
const struct timespec& mtime_of(const struct stat& st) { return st.st_mtimespec; }
const struct timespec& atime_of(const struct stat& st) { return st.st_atimespec; }
#else
const struct timespec& mtime_of(const struct stat& st) { return st.st_mtim; }
const struct timespec& atime_of(const struct stat& st) { return st.st_atim; }
#endif

// Below "/", these are kernel/pseudo file systems or other disks, not the startup volume's content.
bool skip_below_root(const std::string& path) {
    static const char* const skipped[] = {
#if defined(__APPLE__)
        "/dev", "/Volumes", "/System/Volumes/VM", "/System/Volumes/Preboot", "/System/Volumes/Update",
        "/System/Volumes/xarts", "/System/Volumes/iSCPreboot", "/System/Volumes/Hardware", "/private/var/vm",
#else
        "/proc", "/sys", "/dev", "/run", "/mnt", "/media", "/snap",
#endif
    };
    for (const char* s : skipped)
        if (path == s) return true;
    return false;
}

struct DevIno {
    dev_t dev;
    ino_t ino;
    bool operator==(const DevIno& o) const { return dev == o.dev && ino == o.ino; }
};
struct DevInoHash {
    size_t operator()(const DevIno& k) const {
        return std::hash<uint64_t>()(static_cast<uint64_t>(k.ino) * 1000003u ^ static_cast<uint64_t>(k.dev));
    }
};

class Walker {
public:
    Walker(es_scan& scan, bool from_root) : scan_(scan), from_root_(from_root) {}

    void run(uint32_t root_node, std::string root_path, DevIno root_id) {
        seen_.insert(root_id);
        stack_.push_back({root_node, std::move(root_path)});
        pending_ = 1;
        unsigned threads = scan_.thread_count;
        if (threads == 0) threads = std::clamp(std::thread::hardware_concurrency(), 2u, 16u);
        std::vector<std::thread> workers;
        for (unsigned i = 0; i < threads; ++i) workers.emplace_back([this] { worker_loop(); });
        for (auto& t : workers) t.join();
    }

private:
    struct WorkItem {
        uint32_t node;
        std::string path;
    };

    void worker_loop() {
        std::vector<EntryInfo> entries;
        std::vector<std::u16string> names;
        std::vector<std::string> raw_names;
        for (;;) {
            WorkItem item;
            {
                std::unique_lock lock(queue_mutex_);
                queue_cv_.wait(lock, [this] { return !stack_.empty() || pending_ == 0; });
                if (stack_.empty()) return;
                item = std::move(stack_.back());
                stack_.pop_back();
            }
            if (!scan_.cancel.load(std::memory_order_relaxed)) list_directory(item, entries, names, raw_names);
            std::lock_guard lock(queue_mutex_);
            if (--pending_ == 0) queue_cv_.notify_all();
        }
    }

    void list_directory(const WorkItem& item, std::vector<EntryInfo>& entries, std::vector<std::u16string>& names,
                        std::vector<std::string>& raw_names) {
        entries.clear();
        names.clear();
        raw_names.clear();

        const int fd = open(item.path.c_str(), O_RDONLY | O_DIRECTORY | O_CLOEXEC);
        DIR* dir = fd >= 0 ? fdopendir(fd) : nullptr;
        if (!dir) {
            if (fd >= 0) close(fd);
            fail(item.node);
            return;
        }

        std::vector<bool> descend;
        uint64_t bytes = 0, files = 0, dirs = 0;
        while (dirent* d = readdir(dir)) {
            if (std::strcmp(d->d_name, ".") == 0 || std::strcmp(d->d_name, "..") == 0) continue;
            struct stat st{};
            if (fstatat(dirfd(dir), d->d_name, &st, AT_SYMLINK_NOFOLLOW) != 0) continue; // vanished meanwhile

            EntryInfo e;
            e.mtime = to_unix_ms(mtime_of(st));
            e.atime = to_unix_ms(atime_of(st));
            e.attributes = static_cast<uint32_t>(st.st_mode);
            bool go_down = false;
            if (S_ISDIR(st.st_mode)) {
                e.flags = ES_NODE_DIRECTORY;
                std::string child = join(item.path, d->d_name);
                if ((from_root_ && skip_below_root(child)) || !first_visit({st.st_dev, st.st_ino})) e.flags |= ES_NODE_REPARSE;
                else go_down = true;
                ++dirs;
            } else {
                e.size = static_cast<uint64_t>(st.st_size);
                e.allocated = static_cast<uint64_t>(st.st_blocks) * 512;
                if (S_ISREG(st.st_mode) && st.st_nlink > 1 && !first_visit({st.st_dev, st.st_ino})) {
                    e.flags = ES_NODE_HARDLINK;
                    e.size = e.allocated = 0;
                }
                bytes += e.size;
                ++files;
            }
            raw_names.emplace_back(d->d_name);
            names.push_back(utf8_to_utf16(d->d_name));
            entries.push_back(e);
            descend.push_back(go_down);
            if (scan_.cancel.load(std::memory_order_relaxed)) break;
        }
        closedir(dir); // also closes fd

        for (size_t i = 0; i < entries.size(); ++i) entries[i].name = names[i];
        uint32_t first;
        {
            std::lock_guard lock(store_mutex_);
            first = scan_.store.append_children(item.node, entries.data(), entries.size());
        }
        scan_.files.fetch_add(files, std::memory_order_relaxed);
        scan_.directories.fetch_add(dirs, std::memory_order_relaxed);
        scan_.bytes.fetch_add(bytes, std::memory_order_relaxed);
        scan_.set_current_path(utf8_to_utf16(item.path));

        std::lock_guard lock(queue_mutex_);
        for (size_t i = 0; i < entries.size(); ++i) {
            if (!descend[i]) continue;
            stack_.push_back({first + static_cast<uint32_t>(i), join(item.path, raw_names[i])});
            ++pending_;
        }
        queue_cv_.notify_all();
    }

    bool first_visit(DevIno id) {
        std::lock_guard lock(seen_mutex_);
        return seen_.insert(id).second;
    }

    void fail(uint32_t node) {
        scan_.errors.fetch_add(1, std::memory_order_relaxed);
        std::lock_guard lock(store_mutex_);
        scan_.store.mark_error(node);
    }

    static std::string join(const std::string& dir, std::string_view name) {
        std::string p = dir;
        if (p.empty() || p.back() != '/') p.push_back('/');
        p.append(name);
        return p;
    }

    es_scan& scan_;
    const bool from_root_;
    std::mutex store_mutex_, queue_mutex_, seen_mutex_;
    std::condition_variable queue_cv_;
    std::vector<WorkItem> stack_;
    size_t pending_ = 0;
    std::unordered_set<DevIno, DevInoHash> seen_; // directories and hard-linked files
};

} // namespace

es_status scan(es_scan& scan) {
    if (scan.requested_mode == ES_MODE_MFT) return ES_ERR_NOT_SUPPORTED;
    std::string root = utf16_to_utf8(scan.root);
    while (root.size() > 1 && root.back() == '/') root.pop_back();

    struct stat st{};
    if (stat(root.c_str(), &st) != 0) return errno == EACCES ? ES_ERR_ACCESS_DENIED : ES_ERR_NOT_FOUND;
    if (!S_ISDIR(st.st_mode)) return ES_ERR_INVALID_ARG;

    const std::u16string root16 = utf8_to_utf16(root);
    EntryInfo root_info;
    root_info.name = root16;
    root_info.flags = ES_NODE_DIRECTORY;
    root_info.mtime = to_unix_ms(mtime_of(st));
    root_info.atime = to_unix_ms(atime_of(st));
    root_info.attributes = static_cast<uint32_t>(st.st_mode);
    const uint32_t root_node = scan.store.add_root(root_info);

    scan.mode_used = ES_MODE_WALKER;
    scan.phase = ES_PHASE_ENUMERATING;
    Walker(scan, root == "/").run(root_node, root, {st.st_dev, st.st_ino});
    return scan.cancel.load() ? ES_ERR_CANCELLED : ES_OK;
}

bool is_elevated() { return geteuid() == 0; }

#if !defined(__APPLE__)
es_status move_to_trash(std::u16string_view) { return ES_ERR_NOT_SUPPORTED; } // .NET uses `gio trash` on Linux
#endif

} // namespace emmascan::platform
