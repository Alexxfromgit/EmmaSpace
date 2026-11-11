// macOS / Linux backend: a parallel directory walker.
//
// - Listing: on macOS one getattrlistbulk() call describes a whole batch of entries; elsewhere (and on
//   macOS file systems without it, or with EMMASCAN_NO_BULK=1) readdir() plus one fstatat() per entry.
//   Both feed the same add_entry(), so every rule below applies to both.
// - Sizes: st_size is the logical size, st_blocks * 512 what is allocated on disk.
// - Hard links: files with st_nlink > 1 are counted once (later links get ES_NODE_HARDLINK, size 0).
// - Directories reached twice (macOS firmlinks such as /Users and /System/Volumes/Data/Users
//   share the same device and inode) are listed once; later sightings are flagged as links.
// - Symbolic links are recorded but never followed. On macOS, autofs triggers (e.g. /Network) are
//   recorded but never looked into, because that would mount them.
// - Online-only iCloud / File Provider items ("dataless") count only their local bytes; such folders
//   are not listed, and the worker threads refuse to materialize anything, so a scan never downloads.
// - When scanning "/", pseudo file systems and other mounted volumes are skipped.

#include "../scan_state.h"
#include "../utf8.h"
#include "posix_entry.h"
#if defined(__APPLE__)
#include "attr_bulk.h"
#else
#include "ext4_reader.h"
#endif

#include <dirent.h>
#include <fcntl.h>
#include <sys/stat.h>
#if defined(__APPLE__)
#include <sys/attr.h>
#include <sys/resource.h>
#include <sys/vnode.h>
#endif
#include <unistd.h>

#include <algorithm>
#include <atomic>
#include <cerrno>
#include <condition_variable>
#include <cstring>
#include <cstdlib>
#include <fstream>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
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

#if defined(__APPLE__)
// attr_bulk.h carries its own copies of these so it can be tested anywhere; they must match the system's.
static_assert(bulk::kCmnName == ATTR_CMN_NAME && bulk::kCmnDevId == ATTR_CMN_DEVID &&
              bulk::kCmnObjType == ATTR_CMN_OBJTYPE && bulk::kCmnModTime == ATTR_CMN_MODTIME &&
              bulk::kCmnAccTime == ATTR_CMN_ACCTIME && bulk::kCmnAccessMask == ATTR_CMN_ACCESSMASK &&
              bulk::kCmnFlags == ATTR_CMN_FLAGS && bulk::kCmnFileId == ATTR_CMN_FILEID &&
              bulk::kCmnError == ATTR_CMN_ERROR && bulk::kCmnReturnedAttrs == ATTR_CMN_RETURNED_ATTRS);
static_assert(bulk::kDirMountStatus == ATTR_DIR_MOUNTSTATUS && bulk::kFileLinkCount == ATTR_FILE_LINKCOUNT &&
              bulk::kFileAllocSize == ATTR_FILE_ALLOCSIZE && bulk::kFileDataLength == ATTR_FILE_DATALENGTH);
static_assert(bulk::kTypeDirectory == VDIR && bulk::kMountPoint == DIR_MNTSTATUS_MNTPOINT &&
              bulk::kMountTrigger == DIR_MNTSTATUS_TRIGGER && bulk::kFlagFirmlink == SF_FIRMLINK);
static_assert(bulk::kExtPrivateSize == ATTR_CMNEXT_PRIVATESIZE && bulk::kExtCloneId == ATTR_CMNEXT_CLONEID &&
              bulk::kExtFlags == ATTR_CMNEXT_EXT_FLAGS && bulk::kMayShareBlocks == EF_MAY_SHARE_BLOCKS &&
              bulk::kSharesAllBlocks == EF_SHARES_ALL_BLOCKS);
static_assert(sizeof(attribute_set_t) == 5 * sizeof(uint32_t) && sizeof(attrreference_t) == 8 &&
              sizeof(dev_t) == sizeof(int32_t) && sizeof(fsobj_type_t) == sizeof(uint32_t) &&
              sizeof(struct timespec) == 16 && sizeof(off_t) == 8);

// Each worker reads folders in batches of this many bytes (a few thousand entries).
constexpr size_t kBulkBufferSize = 256 * 1024;

// Cleared the first time the kernel refuses FSOPT_ATTR_CMN_EXTENDED (clone attributes), so later
// folders are listed without asking for them.
std::atomic<bool> g_bulk_extended{true};

// EMMASCAN_NO_BULK=1 lists with readdir + fstatat instead, for comparisons and as a way out if a file
// system ever reports something wrong through getattrlistbulk.
bool bulk_enabled() {
    static const bool enabled = [] {
        const char* v = std::getenv("EMMASCAN_NO_BULK");
        return v == nullptr || *v == '\0' || std::strcmp(v, "0") == 0;
    }();
    return enabled;
}
#endif

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

// APFS clone attributes of a file, when the listing provided them (macOS).
struct CloneInfo {
    bool may_share = false; // shares some blocks with other files
    bool pure = false;      // shares all of them with the other files of `id`
    uint64_t id = 0;
    uint64_t private_size = 0;
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

    // Per-worker buffers, reused for every folder so listing does not allocate per entry.
    struct Scratch {
        std::vector<EntryInfo> entries;
        std::vector<std::u16string> names;
        std::vector<std::string> raw_names;
        std::vector<bool> descend;
        uint64_t bytes = 0, files = 0, dirs = 0;
#if defined(__APPLE__)
        std::vector<char> bulk_buffer;
#endif
        void clear() {
            entries.clear();
            names.clear();
            raw_names.clear();
            descend.clear();
            bytes = files = dirs = 0;
        }
    };

    void worker_loop() {
#if defined(__APPLE__)
        // Belt and braces next to the SF_DATALESS checks: if something online-only slips through
        // (e.g. it was evicted mid-scan), fail with EDEADLK instead of downloading it.
        setiopolicy_np(IOPOL_TYPE_VFS_MATERIALIZE_DATALESS_FILES, IOPOL_SCOPE_THREAD, IOPOL_MATERIALIZE_DATALESS_FILES_OFF);
#endif
        Scratch scratch;
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
                    list_directory(item, scratch);
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

    // Records one entry of the folder being listed. `st` is what fstatat() reports for it (or the same
    // values taken from getattrlistbulk). `never_descend` marks an autofs trigger, which must not be looked
    // into because that would mount it.
    void add_entry(const WorkItem& item, std::string_view name, const struct stat& st, bool never_descend,
                   Scratch& s, const CloneInfo& clone = {}) {
        EntryInfo e = describe_entry(st);
        bool go_down = false;
        if (S_ISDIR(st.st_mode)) {
            if (e.flags & ES_NODE_CLOUD) {
                // online-only folder: listing it would fetch it from the cloud
            } else if (never_descend || (from_root_ && skip_below_root(join(item.path, name))) ||
                       !first_visit({st.st_dev, st.st_ino})) {
                e.flags |= ES_NODE_REPARSE;
            } else {
                go_down = true;
            }
            ++s.dirs;
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
            // APFS clones keep their own logical size, but pure clones of one file share every block: the
            // first one seen holds them, the others count only what they own alone (usually nothing).
            if (S_ISREG(st.st_mode) && clone.may_share && !(e.flags & ES_NODE_HARDLINK)) {
                e.flags |= ES_NODE_CLONE;
                if (clone.pure && !first_clone({st.st_dev, static_cast<ino_t>(clone.id)}))
                    e.allocated = std::min(e.allocated, clone.private_size);
            }
            s.bytes += e.size;
            ++s.files;
        }
        s.raw_names.emplace_back(name);
        s.names.push_back(utf8_to_utf16(name));
        s.entries.push_back(e);
        s.descend.push_back(go_down);
    }

    // readdir + one fstatat per entry. Takes ownership of `fd`. False if the folder could not be opened.
    bool list_readdir(const WorkItem& item, int fd, Scratch& s) {
        DIR* dir = fdopendir(fd);
        if (!dir) {
            close(fd);
            return false;
        }
        std::unique_ptr<DIR, int (*)(DIR*)> dir_guard(dir, closedir); // also closes fd
        while (dirent* d = readdir(dir)) {
            if (std::strcmp(d->d_name, ".") == 0 || std::strcmp(d->d_name, "..") == 0) continue;
            struct stat st{};
            if (fstatat(dirfd(dir), d->d_name, &st, AT_SYMLINK_NOFOLLOW) != 0) continue; // vanished meanwhile
            add_entry(item, d->d_name, st, false, s);
            if (scan_.cancel.load(std::memory_order_relaxed)) break;
        }
        return true;
    }

#if defined(__APPLE__)
    enum class Bulk { listed, unsupported, failed };

    // getattrlistbulk: one call describes a whole batch of entries, instead of one fstatat() each.
    // Does not close `fd`.
    Bulk list_bulk(const WorkItem& item, int fd, Scratch& s) {
        attrlist request{};
        request.bitmapcount = ATTR_BIT_MAP_COUNT;
        request.commonattr = bulk::kCommonRequested;
        request.dirattr = bulk::kDirRequested;
        request.fileattr = bulk::kFileRequested;
        if (s.bulk_buffer.empty()) s.bulk_buffer.resize(kBulkBufferSize);

        bool first_batch = true;
        for (;;) {
            const bool extended = g_bulk_extended.load(std::memory_order_relaxed);
            request.forkattr = extended ? bulk::kExtRequested : 0;
            const int count = getattrlistbulk(fd, &request, s.bulk_buffer.data(), s.bulk_buffer.size(),
                                              extended ? FSOPT_ATTR_CMN_EXTENDED : 0);
            if (count < 0) {
                if (errno == EINTR) continue;
                if (errno == EINVAL && extended && first_batch) { // a kernel without the clone attributes
                    g_bulk_extended = false;
                    continue;
                }
                if (!first_batch) return Bulk::listed; // keep what was read, as readdir does when it stops early
                return errno == ENOTSUP || errno == EINVAL ? Bulk::unsupported : Bulk::failed;
            }
            if (count == 0) return Bulk::listed;
            first_batch = false;

            const char* p = s.bulk_buffer.data();
            const char* const end = p + s.bulk_buffer.size();
            for (int i = 0; i < count; ++i) {
                bulk::Record r;
                uint32_t length = 0;
                const auto decoded = bulk::decode_record(p, static_cast<size_t>(end - p), r, length);
                if (decoded == bulk::Decode::malformed) break; // nothing after this can be located
                p += length;
                if (decoded == bulk::Decode::skip) continue;
                add_bulk_entry(item, fd, r, s);
                if (scan_.cancel.load(std::memory_order_relaxed)) return Bulk::listed;
            }
        }
    }

    void add_bulk_entry(const WorkItem& item, int fd, const bulk::Record& r, Scratch& s) {
        const std::string name(r.name);
        const bool trigger = r.is_directory() && (r.mount_status & bulk::kMountTrigger);
        // A firmlink or mount point is described as the link or the covered folder, not as what a path
        // through it reaches. fstatat() reports the latter, which the de-duplication relies on. An entry
        // with an error or missing attributes gets the same treatment. Triggers are never looked up.
        const bool ask_stat = r.error != 0 || !r.complete() ||
                              (r.is_directory() && !trigger &&
                               ((r.flags & bulk::kFlagFirmlink) || (r.mount_status & bulk::kMountPoint)));
        struct stat st{};
        if (ask_stat && !trigger) {
            if (fstatat(fd, name.c_str(), &st, AT_SYMLINK_NOFOLLOW) != 0) return; // vanished meanwhile
        } else {
            st.st_dev = r.dev;
            st.st_ino = r.file_id;
            st.st_mode = static_cast<mode_t>(r.mode);
            st.st_flags = r.flags;
            st.st_nlink = static_cast<nlink_t>(std::min<uint32_t>(r.link_count, std::numeric_limits<nlink_t>::max()));
            st.st_size = std::max<int64_t>(r.data_length, 0);
            st.st_blocks = static_cast<blkcnt_t>((std::max<int64_t>(r.alloc_size, 0) + 511) / 512);
            st.st_mtimespec = {static_cast<time_t>(r.mtime.sec), static_cast<long>(r.mtime.nsec)};
            st.st_atimespec = {static_cast<time_t>(r.atime.sec), static_cast<long>(r.atime.nsec)};
            if (trigger) st.st_mode = (st.st_mode & 07777) | S_IFDIR; // even if the record left the mode out
        }
        CloneInfo clone;
        if (!ask_stat && r.may_share_blocks()) {
            clone.may_share = true;
            clone.pure = r.is_pure_clone();
            clone.id = r.clone_id;
            clone.private_size = (r.ext & bulk::kExtPrivateSize) ? static_cast<uint64_t>(std::max<int64_t>(r.private_size, 0)) : 0;
        }
        add_entry(item, name, st, trigger, s, clone);
    }
#endif

    void list_directory(const WorkItem& item, Scratch& s) {
        s.clear();
        const int fd = open(item.path.c_str(), O_RDONLY | O_DIRECTORY | O_CLOEXEC);
        if (fd < 0) {
            fail(item.node);
            return;
        }
        bool listed = false;
#if defined(__APPLE__)
        if (bulk_enabled()) {
            switch (list_bulk(item, fd, s)) {
            case Bulk::listed:
                listed = true;
                break;
            case Bulk::failed:
                close(fd);
                fail(item.node);
                return;
            case Bulk::unsupported:
                break;
            }
        }
        if (listed) close(fd);
#endif
        if (!listed && !list_readdir(item, fd, s)) {
            fail(item.node);
            return;
        }

        for (size_t i = 0; i < s.entries.size(); ++i) s.entries[i].name = s.names[i];
        uint32_t first;
        {
            std::lock_guard lock(store_mutex_);
            first = scan_.store.append_children(item.node, s.entries.data(), s.entries.size());
        }
        scan_.files.fetch_add(s.files, std::memory_order_relaxed);
        scan_.directories.fetch_add(s.dirs, std::memory_order_relaxed);
        scan_.bytes.fetch_add(s.bytes, std::memory_order_relaxed);
        scan_.set_current_path(utf8_to_utf16(item.path));

        std::lock_guard lock(queue_mutex_);
        for (size_t i = 0; i < s.entries.size(); ++i) {
            if (!s.descend[i]) continue;
            stack_.push_back({first + static_cast<uint32_t>(i), join(item.path, s.raw_names[i])});
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

    bool first_clone(DevIno family) {
        std::lock_guard lock(seen_mutex_);
        return clone_families_.insert(family).second;
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
    std::unordered_set<DevIno, DevInoHash> clone_families_; // (device, clone id) of pure clones seen
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

} // namespace emmascan::platform
