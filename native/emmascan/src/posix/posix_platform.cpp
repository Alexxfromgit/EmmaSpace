// macOS / Linux backend: a parallel directory walker on openat/fdopendir/fstatat.
//
// - Sizes: st_size is the logical size, st_blocks * 512 what is allocated on disk.
// - Hard links: files with st_nlink > 1 are counted once (later links get ES_NODE_HARDLINK, size 0).
// - Directories reached twice (macOS firmlinks such as /Users and /System/Volumes/Data/Users
//   share the same device and inode) are listed once; later sightings are flagged as links.
// - Symbolic links are recorded but never followed.
// - Online-only iCloud / File Provider items ("dataless") count only their local bytes; such folders
//   are not listed, and the worker threads refuse to materialize anything, so a scan never downloads.
// - When scanning "/", pseudo file systems and other mounted volumes are skipped.

#include "../scan_state.h"
#include "../utf8.h"
#include "posix_entry.h"
#if !defined(__APPLE__)
#include "ext4_reader.h"
#endif

#include <dirent.h>
#include <fcntl.h>
#include <sys/stat.h>
#if defined(__APPLE__)
#include <sys/resource.h>
#include <sys/mount.h>
#include <sys/param.h>
#else
#include <sys/statfs.h>
#include <sys/sysmacros.h>
#endif
#include <unistd.h>

#include <climits>
#include <cstdlib>

#include <algorithm>
#include <atomic>
#include <cerrno>
#include <condition_variable>
#include <cstring>
#include <fstream>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

namespace emmascan::platform {
namespace {

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
#if defined(__APPLE__)
    // Firmlinked folders (/Users, /usr/local, /System/Library/Caches, ...) are also reachable through
    // /System/Volumes/Data. Skip that copy so their content is always credited to the familiar path,
    // instead of to whichever path a worker thread happens to reach first.
    static const std::unordered_set<std::string> data_twins = [] {
        std::unordered_set<std::string> twins;
        std::ifstream in("/usr/share/firmlinks");
        for (std::string line; std::getline(in, line);) {
            const auto tab = line.find('\t');
            if (tab != std::string::npos && tab + 1 < line.size())
                twins.insert("/System/Volumes/Data/" + line.substr(tab + 1));
        }
        return twins;
    }();
    if (data_twins.contains(path)) return true;
#endif
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

    // Returns ES_OK, or the first failure of any worker (e.g. ES_ERR_NO_MEMORY).
    es_status run(uint32_t root_node, std::string root_path, DevIno root_id) {
        seen_.insert(root_id);
        stack_.push_back({root_node, std::move(root_path)});
        pending_ = 1;
        unsigned threads = scan_.thread_count;
        if (threads == 0) threads = std::clamp(std::thread::hardware_concurrency(), 2u, 16u);
        std::vector<std::thread> workers;
        try {
            workers.reserve(threads);
            for (unsigned i = 0; i < threads; ++i) workers.emplace_back([this] { worker_loop(); });
        } catch (...) {
            record_failure(ES_ERR_INTERNAL); // stop the workers that did start, then report
        }
        if (workers.empty()) return ES_ERR_INTERNAL;
        for (auto& t : workers) t.join();
        return static_cast<es_status>(failure_.load());
    }

private:
    struct WorkItem {
        uint32_t node;
        std::string path;
    };

    void worker_loop() {
#if defined(__APPLE__)
        // Belt and braces next to the SF_DATALESS checks: if something online-only slips through
        // (e.g. it was evicted mid-scan), fail with EDEADLK instead of downloading it.
        setiopolicy_np(IOPOL_TYPE_VFS_MATERIALIZE_DATALESS_FILES, IOPOL_SCOPE_THREAD, IOPOL_MATERIALIZE_DATALESS_FILES_OFF);
#endif
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
            // Exceptions must not escape a worker thread (std::terminate), and the item must
            // always be retired so the other workers can finish.
            if (!scan_.cancel.load(std::memory_order_relaxed)) {
                try {
                    list_directory(item, entries, names, raw_names);
                } catch (const std::bad_alloc&) {
                    record_failure(ES_ERR_NO_MEMORY);
                } catch (...) {
                    record_failure(ES_ERR_INTERNAL);
                }
            }
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
        std::unique_ptr<DIR, int (*)(DIR*)> dir_guard(dir, closedir); // also closes fd

        std::vector<bool> descend;
        uint64_t bytes = 0, files = 0, dirs = 0;
        while (dirent* d = readdir(dir)) {
            if (std::strcmp(d->d_name, ".") == 0 || std::strcmp(d->d_name, "..") == 0) continue;
            struct stat st{};
            if (fstatat(dirfd(dir), d->d_name, &st, AT_SYMLINK_NOFOLLOW) != 0) continue; // vanished meanwhile

            EntryInfo e = describe_entry(st);
            bool go_down = false;
            if (S_ISDIR(st.st_mode)) {
                std::string child = join(item.path, d->d_name);
                if (e.flags & ES_NODE_CLOUD) {
                    // online-only folder: listing it would fetch it from the cloud
                } else if ((from_root_ && skip_below_root(child)) || !first_visit({st.st_dev, st.st_ino})) {
                    e.flags |= ES_NODE_REPARSE;
                } else {
                    go_down = true;
                }
                ++dirs;
            } else {
                // Symlinks are flagged as reparse nodes by describe_entry().
                if (S_ISREG(st.st_mode) && st.st_nlink > 1) {
                    if (first_visit({st.st_dev, st.st_ino})) {
                        e.flags |= ES_NODE_HAS_LINKS; // counted here; other names may exist
                    } else {
                        e.flags |= ES_NODE_HARDLINK;
                        e.size = e.allocated = 0;
                    }
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
        dir_guard.reset();

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

    void record_failure(es_status status) {
        int expected = ES_OK;
        failure_.compare_exchange_strong(expected, status);
        scan_.cancel = true; // the other workers drain the queue without listing
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
    std::atomic<int> failure_{ES_OK};
};

} // namespace

es_status scan(es_scan& scan) {
    if (scan.requested_mode == ES_MODE_MFT) return ES_ERR_NOT_SUPPORTED;
#if defined(__APPLE__)
    if (scan.requested_mode == ES_MODE_EXT4) return ES_ERR_NOT_SUPPORTED;
#endif
    std::string root = utf16_to_utf8(scan.root);
    while (root.size() > 1 && root.back() == '/') root.pop_back();

    struct stat st{};
    if (stat(root.c_str(), &st) != 0) return errno == EACCES ? ES_ERR_ACCESS_DENIED : ES_ERR_NOT_FOUND;
    if (!S_ISDIR(st.st_mode)) return ES_ERR_INVALID_ARG;

#if !defined(__APPLE__)
    if (scan.requested_mode == ES_MODE_EXT4) return ext4_scan(scan, root);
    // Auto: reading ext4's metadata is far faster for a whole file system but needs root. It always reads
    // every inode table, so for a subfolder walking that folder is quicker.
    if (scan.requested_mode == ES_MODE_AUTO && is_elevated() && is_ext4_mount_root(root)) {
        const es_status status = ext4_scan(scan, root);
        if (status == ES_OK || status == ES_ERR_CANCELLED || scan.cancel.load()) return status;
        scan.store = NodeStore{};
        scan.files = scan.directories = scan.bytes = 0;
        scan.errors = 0;
    }
#endif

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
    const es_status status = Walker(scan, root == "/").run(root_node, root, {st.st_dev, st.st_ino});
    if (status != ES_OK) return status;
    return scan.cancel.load() ? ES_ERR_CANCELLED : ES_OK;
}

bool is_elevated() { return geteuid() == 0; }

#if !defined(__APPLE__)
es_status move_to_trash(std::u16string_view) { return ES_ERR_NOT_SUPPORTED; } // .NET uses `gio trash` on Linux
#endif

namespace {

int64_t to_unix_ns(const struct timespec& t) { return static_cast<int64_t>(t.tv_sec) * 1000000000LL + t.tv_nsec; }

} // namespace

es_status file_stamp(intptr_t handle, es_file_stamp& stamp) {
    if (handle < 0 || handle > INT_MAX) return ES_ERR_INVALID_ARG;
    struct stat st{};
    if (fstat(static_cast<int>(handle), &st) != 0) return errno == EBADF ? ES_ERR_INVALID_ARG : ES_ERR_IO;
    stamp.volume = static_cast<uint64_t>(st.st_dev);
    stamp.file_id_low = static_cast<uint64_t>(st.st_ino);
    stamp.size = static_cast<uint64_t>(st.st_size);
    stamp.mtime_ns = to_unix_ns(mtime_of(st));
    stamp.ctime_ns = to_unix_ns(ctime_of(st)); // set by the kernel on every change; utimes() can't move it back
    if (stamp.ctime_ns != 0) stamp.flags |= ES_STAMP_TRUSTED;
    return ES_OK;
}

#if defined(__APPLE__)
// In storage_mac.cpp: "Medium Type" of the disk behind a BSD device such as "/dev/disk3s1".
uint32_t mac_medium_type(const char* bsd_device);

uint32_t storage_kind(std::u16string_view path, uint64_t& device) {
    device = 0;
    const std::string p = utf16_to_utf8(path);
    struct stat st{};
    if (stat(p.c_str(), &st) != 0) return ES_STORAGE_UNKNOWN;
    device = static_cast<uint64_t>(st.st_dev);
    struct statfs fs{};
    if (statfs(p.c_str(), &fs) != 0) return ES_STORAGE_UNKNOWN;
    for (const char* network : {"smbfs", "nfs", "afpfs", "webdav", "cifs", "ftp"})
        if (std::strcmp(fs.f_fstypename, network) == 0) return ES_STORAGE_NETWORK;
    if ((fs.f_flags & MNT_LOCAL) == 0) return ES_STORAGE_NETWORK;
    if (std::strncmp(fs.f_mntfromname, "/dev/", 5) != 0) return ES_STORAGE_UNKNOWN;

    // Asking the I/O Registry takes far longer than the rest, and the finder asks about every folder:
    // remember the answer per disk.
    static std::mutex cache_mutex;
    static std::unordered_map<std::string, uint32_t> by_disk;
    const std::string disk = fs.f_mntfromname;
    {
        std::lock_guard lock(cache_mutex);
        if (const auto known = by_disk.find(disk); known != by_disk.end()) return known->second;
    }
    const uint32_t kind = mac_medium_type(disk.c_str());
    std::lock_guard lock(cache_mutex);
    by_disk.emplace(disk, kind);
    return kind;
}
#else
namespace {

constexpr uint32_t kFuseSuperMagic = 0x65735546u;

// FUSE file systems that fetch their data over the network (the type in mountinfo is "fuse.<name>").
bool is_network_fuse(std::string_view name) {
    for (std::string_view network : {"sshfs", "rclone", "s3fs", "gcsfuse", "goofys", "blobfuse", "blobfuse2",
                                     "gvfsd-fuse", "smbnetfs", "curlftpfs", "davfs", "httpfs2", "afpfs"})
        if (name == network) return true;
    return false;
}

// The type (with its FUSE subtype, e.g. "fuse.sshfs") and source (e.g. "/dev/nvme0n1p2") of the mounted
// file system with this device number, from /proc/self/mountinfo, whose lines read
// "36 35 98:0 /mnt1 /mnt2 rw,noatime master:1 - ext3 /dev/root rw,errors=continue".
bool find_mount(dev_t dev, std::string& type, std::string& source) {
    std::ifstream in("/proc/self/mountinfo");
    const std::string id = std::to_string(major(dev)) + ":" + std::to_string(minor(dev));
    for (std::string line; std::getline(in, line);) {
        std::istringstream fields(line);
        std::string mount_id, parent_id, major_minor;
        if (!(fields >> mount_id >> parent_id >> major_minor) || major_minor != id) continue;
        const auto dash = line.find(" - ");
        if (dash == std::string::npos) continue;
        std::istringstream tail(line.substr(dash + 3));
        if (tail >> type >> source) return true;
    }
    return false;
}

std::string resolved(const std::string& path) {
    char buffer[PATH_MAX];
    return realpath(path.c_str(), buffer) ? std::string(buffer) : std::string();
}

// queue/rotational of a block device's sysfs directory; a partition has no queue of its own, its disk
// (the parent directory) does. -1 when there is none.
int rotational_of(std::string device_dir) {
    for (int level = 0; level < 2 && device_dir.size() > 1; ++level) {
        std::ifstream in(device_dir + "/queue/rotational");
        int rotational = 0;
        if (in >> rotational) return rotational != 0 ? 1 : 0;
        device_dir.erase(device_dir.find_last_of('/'));
    }
    return -1;
}

} // namespace

uint32_t storage_kind(std::u16string_view path, uint64_t& device) {
    device = 0;
    const std::string p = utf16_to_utf8(path);
    struct stat st{};
    if (stat(p.c_str(), &st) != 0) return ES_STORAGE_UNKNOWN;
    device = static_cast<uint64_t>(st.st_dev);

    struct statfs fs{};
    const bool have_fs = statfs(p.c_str(), &fs) == 0;
    if (have_fs) {
        switch (static_cast<uint32_t>(fs.f_type)) {
        case 0x6969u:     // NFS
        case 0x517Bu:     // SMB
        case 0xFF534D42u: // CIFS
        case 0xFE534D42u: // SMB2
        case 0x01021997u: // 9P (e.g. WSL's view of Windows drives, VM shared folders)
        case 0x73757245u: // Coda
        case 0x5346414Fu: // AFS
        case 0x00C36400u: // Ceph
            return ES_STORAGE_NETWORK;
        default:
            break;
        }
    }

    // A block device's sysfs entry is found by its number. File systems on an anonymous device (btrfs) are
    // found through the device they were mounted from; FUSE ones by their subtype.
    std::string device_dir = resolved("/sys/dev/block/" + std::to_string(major(st.st_dev)) + ":" +
                                      std::to_string(minor(st.st_dev)));
    const bool fuse = have_fs && static_cast<uint32_t>(fs.f_type) == kFuseSuperMagic;
    if (device_dir.empty() || fuse) {
        std::string type, source;
        if (!find_mount(st.st_dev, type, source)) return ES_STORAGE_UNKNOWN;
        if (type.rfind("fuse.", 0) == 0 && is_network_fuse(std::string_view(type).substr(5))) return ES_STORAGE_NETWORK;
        if (device_dir.empty() && source.rfind("/dev/", 0) == 0) {
            const std::string node = resolved(source); // e.g. /dev/mapper/root -> /dev/dm-0
            if (!node.empty()) device_dir = resolved("/sys/class/block/" + node.substr(node.find_last_of('/') + 1));
        }
    }
    if (device_dir.empty()) return ES_STORAGE_UNKNOWN;
    const int rotational = rotational_of(device_dir);
    return rotational < 0 ? ES_STORAGE_UNKNOWN : rotational ? ES_STORAGE_HDD : ES_STORAGE_SSD;
}
#endif

} // namespace emmascan::platform
