// Fast scan for ext2/ext3/ext4 on Linux: reads every inode table and directory block from the block
// device instead of opening each folder (the counterpart of the NTFS master file table reader).
//
// - Needs root to open the device.
// - Reads are buffered on purpose: ext4 keeps its metadata in the block device's page cache, so buffered
//   reads see the current state, including changes not yet written back. syncfs() first gets file sizes
//   of delayed-allocation writes onto the inodes.
// - Before trusting the device, its superblock is checked against statfs() of the folder (UUID-derived
//   file system ID, inode count, block size), so a wrong device is never parsed.
// - Other file systems mounted below the root are recorded as mount points and not descended, like the
//   walker skips other volumes.

#include "ext4_reader.h"

#include "../ext4/ext4_parser.h"
#include "../utf8.h"

#include <fcntl.h>
#include <sys/stat.h>
#include <sys/statfs.h>
#include <sys/sysmacros.h>
#include <unistd.h>

#include <atomic>
#include <cerrno>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <memory>
#include <sstream>
#include <string_view>
#include <thread>
#include <utility>
#include <vector>

namespace emmascan::platform {
namespace {

struct Mount {
    unsigned major = 0, minor = 0;
    std::string mount_point;
    std::string fstype;
    std::string source;
};

// /proc/self/mountinfo escapes space, tab, newline and backslash as \ooo.
std::string unescape(const std::string& s) {
    std::string out;
    for (size_t i = 0; i < s.size(); ++i) {
        if (s[i] == '\\' && i + 3 < s.size() && s[i + 1] >= '0' && s[i + 1] <= '3' &&
            s[i + 2] >= '0' && s[i + 2] <= '7' && s[i + 3] >= '0' && s[i + 3] <= '7') {
            out.push_back(static_cast<char>((s[i + 1] - '0') * 64 + (s[i + 2] - '0') * 8 + (s[i + 3] - '0')));
            i += 3;
        } else {
            out.push_back(s[i]);
        }
    }
    return out;
}

std::vector<Mount> read_mounts() {
    std::vector<Mount> mounts;
    std::ifstream in("/proc/self/mountinfo");
    for (std::string line; std::getline(in, line);) {
        // id parent major:minor root mount-point options [optional...] - fstype source super-options
        std::istringstream fields(line);
        std::string id, parent, dev, root, point;
        if (!(fields >> id >> parent >> dev >> root >> point)) continue;
        std::string word;
        while (fields >> word && word != "-") {
        }
        Mount m;
        if (!(fields >> m.fstype >> m.source)) continue;
        if (std::sscanf(dev.c_str(), "%u:%u", &m.major, &m.minor) != 2) continue;
        m.mount_point = unescape(point);
        m.source = unescape(m.source);
        mounts.push_back(std::move(m));
    }
    return mounts;
}

bool is_ext_fstype(const std::string& t) { return t == "ext4" || t == "ext3" || t == "ext2"; }

std::string real_path(const std::string& path) {
    std::unique_ptr<char, decltype(&std::free)> resolved(realpath(path.c_str(), nullptr), &std::free);
    return resolved ? std::string(resolved.get()) : path;
}

bool is_below(const std::string& path, const std::string& root) {
    if (root == "/") return path.size() > 1 && path[0] == '/';
    return path.size() > root.size() && path.compare(0, root.size(), root) == 0 && path[root.size()] == '/';
}

class FdSource final : public ext4::BlockSource {
public:
    explicit FdSource(int fd) : fd_(fd) {}
    ~FdSource() override { close(fd_); }
    FdSource(const FdSource&) = delete;
    FdSource& operator=(const FdSource&) = delete;

    bool read(uint64_t offset, void* buffer, size_t length) override {
        auto* p = static_cast<uint8_t*>(buffer);
        while (length > 0) {
            const ssize_t n = pread(fd_, p, length, static_cast<off_t>(offset));
            if (n < 0 && errno == EINTR) continue;
            if (n <= 0) return false;
            p += n;
            offset += static_cast<uint64_t>(n);
            length -= static_cast<size_t>(n);
        }
        return true;
    }

private:
    int fd_;
};

// statfs() derives f_fsid from the superblock UUID (fs/ext4/super.c ext4_statfs).
bool same_file_system(const ext4::Superblock& sb, const struct statfs& fs) {
    uint64_t lo = 0, hi = 0;
    for (int i = 7; i >= 0; --i) lo = (lo << 8) | sb.uuid[i];
    for (int i = 15; i >= 8; --i) hi = (hi << 8) | sb.uuid[i];
    const uint64_t fsid = lo ^ hi;
    int words[2];
    static_assert(sizeof(words) == sizeof(fs.f_fsid));
    std::memcpy(words, &fs.f_fsid, sizeof(words));
    return static_cast<uint32_t>(words[0]) == static_cast<uint32_t>(fsid) &&
           static_cast<uint32_t>(words[1]) == static_cast<uint32_t>(fsid >> 32) &&
           static_cast<uint64_t>(fs.f_files) == sb.inode_count && static_cast<uint64_t>(fs.f_bsize) == sb.block_size;
}

void set_progress(es_scan& scan, const char16_t* text) { scan.set_current_path(text); }

// syncfs() so that sizes of files still being written reach their inodes. It can take long on a busy file
// system and cannot be interrupted, so it runs on its own thread and a cancelled scan stops waiting for it.
bool sync_file_system(const std::string& path, const std::atomic<bool>& cancel) {
    const int dir = open(path.c_str(), O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    if (dir < 0) return true;
    auto done = std::make_shared<std::atomic<bool>>(false);
    try {
        std::thread([dir, done] {
            syncfs(dir);
            close(dir);
            done->store(true);
        }).detach();
    } catch (...) {
        syncfs(dir);
        close(dir);
        return !cancel.load();
    }
    while (!done->load()) {
        if (cancel.load(std::memory_order_relaxed)) return false;
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    return true;
}

} // namespace

bool is_ext4_mount_root(const std::string& root) {
    const std::string real = real_path(root);
    bool found = false;
    for (const Mount& m : read_mounts()) // the last mount on a path is the one that is visible
        if (m.mount_point == real) found = is_ext_fstype(m.fstype);
    return found;
}

es_status ext4_scan(es_scan& scan, const std::string& root) {
    const std::string real = real_path(root);
    struct stat st{};
    if (stat(real.c_str(), &st) != 0) return errno == EACCES ? ES_ERR_ACCESS_DENIED : ES_ERR_NOT_FOUND;
    if (!S_ISDIR(st.st_mode)) return ES_ERR_INVALID_ARG;
    if (st.st_ino > UINT32_MAX) return ES_ERR_NOT_SUPPORTED;

    // The mount that holds the folder: same device, longest mount point that contains it.
    const std::vector<Mount> mounts = read_mounts();
    const Mount* mount = nullptr;
    for (const Mount& m : mounts) {
        if (makedev(m.major, m.minor) != st.st_dev) continue;
        if (m.mount_point != real && !is_below(real, m.mount_point)) continue;
        if (!mount || m.mount_point.size() >= mount->mount_point.size()) mount = &m;
    }
    if (!mount || !is_ext_fstype(mount->fstype)) return ES_ERR_NOT_SUPPORTED;

    struct statfs fs{};
    if (statfs(real.c_str(), &fs) != 0) return ES_ERR_NOT_SUPPORTED;

    const std::string by_number = "/dev/block/" + std::to_string(mount->major) + ":" + std::to_string(mount->minor);
    int fd = open(by_number.c_str(), O_RDONLY | O_CLOEXEC);
    if (fd < 0 && errno != EACCES && errno != EPERM && mount->source.starts_with("/dev/"))
        fd = open(mount->source.c_str(), O_RDONLY | O_CLOEXEC);
    if (fd < 0) return (errno == EACCES || errno == EPERM) ? ES_ERR_ACCESS_DENIED : ES_ERR_NOT_SUPPORTED;
    struct stat dev_st{};
    if (fstat(fd, &dev_st) != 0 || !S_ISBLK(dev_st.st_mode) || dev_st.st_rdev != st.st_dev) {
        close(fd);
        return ES_ERR_NOT_SUPPORTED;
    }
    FdSource source(fd);

    uint8_t super[ext4::kSuperblockSize];
    ext4::Superblock sb;
    if (!source.read(ext4::kSuperblockOffset, super, sizeof(super)) ||
        ext4::parse_superblock(super, sizeof(super), sb) != ext4::SuperblockCheck::Ok || !same_file_system(sb, fs))
        return ES_ERR_NOT_SUPPORTED;

    scan.mode_used = ES_MODE_EXT4;
    scan.phase = ES_PHASE_ENUMERATING;
    set_progress(scan, u"Writing pending changes to disk…");
    if (!sync_file_system(real, scan.cancel)) return ES_ERR_CANCELLED;

    set_progress(scan, u"Reading the file system's inode tables…");
    ext4::Ext4Index index;
    ext4::ReadOptions options;
    options.threads = scan.thread_count;
    options.cancel = &scan.cancel;
    options.inodes_read = &scan.files; // inodes read, shown as progress
    if (const es_status status = index.load(source, options); status != ES_OK) return status;

    // Mount points below the root, as folder inodes on this file system. They are looked up by name in what was
    // just read, starting at the root: stat() would follow bind mounts and name the wrong folder. A path that
    // leaves this file system (it crosses another mount) is not reachable in the tree, so it needs no entry.
    std::vector<uint32_t> skip;
    for (const Mount& m : mounts) {
        if (!is_below(m.mount_point, real)) continue;
        auto ino = static_cast<uint32_t>(st.st_ino);
        std::string_view rest = std::string_view(m.mount_point).substr(real == "/" ? 1 : real.size() + 1);
        for (size_t slash; ino != 0 && (slash = rest.find('/')) != std::string_view::npos; rest.remove_prefix(slash + 1))
            ino = index.find_child(ino, rest.substr(0, slash));
        if (ino != 0 && !rest.empty()) ino = index.find_child(ino, rest);
        if (ino != 0) skip.push_back(ino);
    }

    set_progress(scan, u"Building the folder tree…");
    uint64_t files = 0, dirs = 0, bytes = 0;
    uint32_t errors = 0;
    const std::u16string root16 = utf8_to_utf16(root);
    if (!index.build_tree(static_cast<uint32_t>(st.st_ino), root16, skip, scan.store, scan.cancel, files, dirs, bytes,
                          errors))
        return scan.cancel.load() ? ES_ERR_CANCELLED : ES_ERR_NOT_FOUND;
    scan.files = files;
    scan.directories = dirs;
    scan.bytes = bytes;
    scan.errors = errors;
    return ES_OK;
}

} // namespace emmascan::platform
