// Parallel directory walker for Windows. Works without elevation.
//
// Each directory is listed with GetFileInformationByHandleEx(FileIdBothDirectoryInfo), the
// documented Win32 wrapper over NtQueryDirectoryFile, which returns sizes, allocation sizes,
// timestamps and attributes for many entries per call. Worker threads pull directories from
// a shared stack; results are appended to the node store under one lock per directory.

#include "../scan_state.h"
#include "win_common.h"

#include <algorithm>
#include <condition_variable>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace emmascan::win {
namespace {

struct WorkItem {
    uint32_t node;
    std::wstring path;
};

std::wstring join_path(const std::wstring& dir, std::wstring_view name) {
    std::wstring result;
    result.reserve(dir.size() + name.size() + 1);
    result = dir;
    if (!result.empty() && result.back() != L'\\') result.push_back(L'\\');
    result.append(name);
    return result;
}

class Walker {
public:
    explicit Walker(es_scan& scan) : scan_(scan) {}

    void run(uint32_t root_node, std::wstring root_path) {
        stack_.push_back({root_node, std::move(root_path)});
        pending_ = 1;

        unsigned threads = scan_.thread_count;
        if (threads == 0) threads = std::clamp(std::thread::hardware_concurrency(), 2u, 32u);
        std::vector<std::thread> workers;
        workers.reserve(threads);
        for (unsigned i = 0; i < threads; ++i) workers.emplace_back([this] { worker_loop(); });
        for (auto& t : workers) t.join();
    }

private:
    void worker_loop() {
        // 64 KiB is the largest buffer the directory query reliably fills on SMB shares.
        constexpr size_t buffer_size = 64 * 1024;
        auto buffer = std::make_unique<std::byte[]>(buffer_size);
        std::vector<EntryInfo> entries;
        std::vector<std::wstring> names; // owns name storage for `entries`

        for (;;) {
            WorkItem item;
            {
                std::unique_lock lock(queue_mutex_);
                queue_cv_.wait(lock, [this] { return !stack_.empty() || pending_ == 0; });
                if (stack_.empty()) return;
                item = std::move(stack_.back());
                stack_.pop_back();
            }

            if (!scan_.cancel.load(std::memory_order_relaxed))
                list_directory(item, buffer.get(), buffer_size, entries, names);

            std::lock_guard lock(queue_mutex_);
            if (--pending_ == 0) queue_cv_.notify_all();
        }
    }

    void list_directory(const WorkItem& item, std::byte* buffer, size_t buffer_size,
                        std::vector<EntryInfo>& entries, std::vector<std::wstring>& names) {
        entries.clear();
        names.clear();

        UniqueHandle dir(CreateFileW(to_long_path(item.path).c_str(), FILE_LIST_DIRECTORY,
                                     FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                                     OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr));
        if (!dir.valid()) {
            fail(item.node);
            return;
        }

        const bool follow_reparse = (scan_.flags & ES_SCAN_FOLLOW_REPARSE) != 0;
        uint64_t bytes = 0, files = 0, dirs = 0;
        FILE_INFO_BY_HANDLE_CLASS info_class = FileIdBothDirectoryRestartInfo;
        for (;;) {
            if (!GetFileInformationByHandleEx(dir.get(), info_class, buffer, static_cast<DWORD>(buffer_size))) {
                const DWORD err = GetLastError();
                if (err != ERROR_NO_MORE_FILES) fail(item.node);
                break;
            }
            info_class = FileIdBothDirectoryInfo;

            auto* p = buffer;
            for (;;) {
                const auto* info = reinterpret_cast<const FILE_ID_BOTH_DIR_INFO*>(p);
                std::wstring_view name(info->FileName, info->FileNameLength / sizeof(wchar_t));
                if (name != L"." && name != L"..") {
                    EntryInfo e;
                    const DWORD attrs = info->FileAttributes;
                    const bool is_dir = (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0;
                    e.attributes = attrs;
                    e.mtime = filetime_to_unix_ms(info->LastWriteTime.QuadPart);
                    e.atime = filetime_to_unix_ms(info->LastAccessTime.QuadPart);
                    if (is_dir) {
                        e.flags = ES_NODE_DIRECTORY;
                        // For reparse points EaSize holds the reparse tag. Only links (junctions,
                        // mount points, symlinks) are skipped; cloud placeholders such as OneDrive
                        // folders hold real local files and are walked.
                        if ((attrs & FILE_ATTRIBUTE_REPARSE_POINT) && !follow_reparse && IsReparseTagNameSurrogate(info->EaSize))
                            e.flags |= ES_NODE_REPARSE;
                        // A cloud folder whose listing is still virtualized: enumerating it would make
                        // the provider fetch it from the network and create placeholders. Its
                        // contents are not stored locally, so record it and do not descend.
                        if (attrs & FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS) e.flags |= ES_NODE_CLOUD;
                        ++dirs;
                    } else {
                        e.size = static_cast<uint64_t>(info->EndOfFile.QuadPart);
                        e.allocated = static_cast<uint64_t>(info->AllocationSize.QuadPart);
                        if (is_cloud_placeholder(attrs)) {
                            e.size = std::min(e.size, e.allocated); // online-only data takes no local space
                            e.flags |= ES_NODE_CLOUD;
                        }
                        bytes += e.size;
                        ++files;
                    }
                    names.emplace_back(name);
                    entries.push_back(e);
                }
                if (info->NextEntryOffset == 0) break;
                p += info->NextEntryOffset;
            }
            if (scan_.cancel.load(std::memory_order_relaxed)) return;
        }

        // `names` may have reallocated while filling, so bind the views only now.
        for (size_t i = 0; i < entries.size(); ++i)
            entries[i].name = std::u16string_view(reinterpret_cast<const char16_t*>(names[i].data()), names[i].size());

        uint32_t first;
        {
            std::lock_guard lock(store_mutex_);
            first = scan_.store.append_children(item.node, entries.data(), entries.size());
        }
        scan_.files.fetch_add(files, std::memory_order_relaxed);
        scan_.directories.fetch_add(dirs, std::memory_order_relaxed);
        scan_.bytes.fetch_add(bytes, std::memory_order_relaxed);
        scan_.set_current_path(std::u16string_view(reinterpret_cast<const char16_t*>(item.path.data()), item.path.size()));

        if (dirs == 0) return;
        std::lock_guard lock(queue_mutex_);
        for (size_t i = 0; i < entries.size(); ++i) {
            const uint16_t f = entries[i].flags;
            if ((f & ES_NODE_DIRECTORY) && !(f & (ES_NODE_REPARSE | ES_NODE_CLOUD))) {
                stack_.push_back({first + static_cast<uint32_t>(i), join_path(item.path, names[i])});
                ++pending_;
            }
        }
        queue_cv_.notify_all();
    }

    void fail(uint32_t node) {
        scan_.errors.fetch_add(1, std::memory_order_relaxed);
        std::lock_guard lock(store_mutex_);
        scan_.store.mark_error(node);
    }

    es_scan& scan_;
    std::mutex store_mutex_;
    std::mutex queue_mutex_;
    std::condition_variable queue_cv_;
    std::vector<WorkItem> stack_; // LIFO keeps the frontier small (depth-first-ish)
    size_t pending_ = 0;          // queued + in-progress directories
};

} // namespace

es_status walk(es_scan& scan, const std::wstring& root_path) {
    WIN32_FILE_ATTRIBUTE_DATA data{};
    if (!GetFileAttributesExW(to_long_path(root_path).c_str(), GetFileExInfoStandard, &data))
        return status_from_win32(GetLastError());
    if (!(data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) return ES_ERR_INVALID_ARG;

    EntryInfo root;
    root.name = std::u16string_view(reinterpret_cast<const char16_t*>(root_path.data()), root_path.size());
    root.attributes = data.dwFileAttributes;
    root.flags = ES_NODE_DIRECTORY;
    root.mtime = filetime_to_unix_ms(data.ftLastWriteTime);
    root.atime = filetime_to_unix_ms(data.ftLastAccessTime);
    const uint32_t root_node = scan.store.add_root(root);

    scan.mode_used = ES_MODE_WALKER;
    scan.phase = ES_PHASE_ENUMERATING;
    Walker(scan).run(root_node, root_path);
    return scan.cancel.load() ? ES_ERR_CANCELLED : ES_OK;
}

} // namespace emmascan::win
