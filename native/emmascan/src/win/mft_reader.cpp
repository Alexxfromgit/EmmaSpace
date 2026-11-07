// Fast scan for NTFS volumes: reads the master file table straight from the volume instead of
// opening every directory. Needs administrator rights (raw volume access).

#include "../ntfs/mft_parser.h"
#include "../scan_state.h"
#include "win_common.h"

#include <winioctl.h>

#include <algorithm>
#include <memory>
#include <string>
#include <vector>

namespace emmascan::win {
namespace {

struct AlignedBuffer {
    explicit AlignedBuffer(size_t size)
        : data(static_cast<uint8_t*>(VirtualAlloc(nullptr, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE))), size(size) {}
    ~AlignedBuffer() {
        if (data) VirtualFree(data, 0, MEM_RELEASE);
    }
    AlignedBuffer(const AlignedBuffer&) = delete;
    AlignedBuffer& operator=(const AlignedBuffer&) = delete;
    uint8_t* data;
    size_t size;
};

bool read_at(HANDLE volume, uint64_t offset, uint8_t* buffer, DWORD length) {
    OVERLAPPED ov{};
    ov.Offset = static_cast<DWORD>(offset);
    ov.OffsetHigh = static_cast<DWORD>(offset >> 32);
    DWORD read = 0;
    return ReadFile(volume, buffer, length, &read, &ov) && read == length;
}

std::wstring trim_trailing_backslash(std::wstring s) {
    while (s.size() > 1 && s.back() == L'\\') s.pop_back();
    return s;
}

bool same_path(const std::wstring& a, const std::wstring& b) {
    return CompareStringOrdinal(a.c_str(), static_cast<int>(a.size()), b.c_str(), static_cast<int>(b.size()), TRUE) == CSTR_EQUAL;
}

void set_progress(es_scan& scan, const wchar_t* text) {
    std::wstring_view v(text);
    scan.set_current_path(std::u16string_view(reinterpret_cast<const char16_t*>(v.data()), v.size()));
}

} // namespace

es_status mft_scan(es_scan& scan, const std::wstring& root_path) {
    // Which volume holds the root, and is it NTFS?
    wchar_t volume_path[MAX_PATH];
    if (!GetVolumePathNameW(to_long_path(root_path).c_str(), volume_path, MAX_PATH)) return status_from_win32(GetLastError());
    wchar_t fs_name[MAX_PATH] = {};
    if (!GetVolumeInformationW(volume_path, nullptr, 0, nullptr, nullptr, nullptr, fs_name, MAX_PATH) ||
        lstrcmpiW(fs_name, L"NTFS") != 0)
        return ES_ERR_NOT_SUPPORTED;
    wchar_t volume_name[MAX_PATH];
    if (!GetVolumeNameForVolumeMountPointW(volume_path, volume_name, MAX_PATH)) return ES_ERR_NOT_SUPPORTED;

    UniqueHandle volume(CreateFileW(trim_trailing_backslash(volume_name).c_str(), GENERIC_READ,
                                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                                    FILE_FLAG_SEQUENTIAL_SCAN, nullptr));
    if (!volume.valid()) return status_from_win32(GetLastError()); // ES_ERR_ACCESS_DENIED when not elevated

    NTFS_VOLUME_DATA_BUFFER info{};
    DWORD returned = 0;
    if (!DeviceIoControl(volume.get(), FSCTL_GET_NTFS_VOLUME_DATA, nullptr, 0, &info, sizeof(info), &returned, nullptr))
        return ES_ERR_NOT_SUPPORTED;
    const uint64_t cluster = info.BytesPerCluster;
    const uint32_t record_size = info.BytesPerFileRecordSegment;
    if (cluster == 0 || record_size < 1024 || record_size % 512 != 0) return ES_ERR_NOT_SUPPORTED;

    scan.mode_used = ES_MODE_MFT;
    scan.phase = ES_PHASE_ENUMERATING;
    set_progress(scan, L"Reading the master file table…");

    // Record 0 is $MFT itself; its $DATA runlist says where the rest of the table lives.
    const size_t first_read = static_cast<size_t>(std::max<uint64_t>(cluster, record_size));
    AlignedBuffer first(first_read);
    if (!first.data || !read_at(volume.get(), static_cast<uint64_t>(info.MftStartLcn.QuadPart) * cluster, first.data,
                                static_cast<DWORD>(first_read)))
        return ES_ERR_IO;
    ntfs::ParsedRecord mft_record;
    if (!ntfs::apply_fixups(first.data, record_size) || !ntfs::parse_record(first.data, record_size, mft_record, true) ||
        mft_record.data_runs.empty())
        return ES_ERR_NOT_SUPPORTED;

    const uint64_t mft_bytes = static_cast<uint64_t>(info.MftValidDataLength.QuadPart);
    uint64_t mapped = 0;
    for (const auto& run : mft_record.data_runs) mapped += run.length * cluster;
    // A $MFT so fragmented that its runlist continues in an extension record is not handled;
    // the caller falls back to the directory walker.
    if (mapped < mft_bytes) return ES_ERR_NOT_SUPPORTED;

    const uint64_t record_count = mft_bytes / record_size;
    ntfs::MftIndex index(record_count);

    constexpr uint64_t chunk_bytes = 8ull << 20;
    AlignedBuffer chunk(static_cast<size_t>(chunk_bytes));
    if (!chunk.data) return ES_ERR_NO_MEMORY;
    ntfs::ParsedRecord record;
    uint64_t frn = 0;
    for (const auto& run : mft_record.data_runs) {
        uint64_t run_bytes = std::min(run.length * cluster, (record_count - frn) * record_size);
        uint64_t run_offset = 0;
        while (run_bytes > 0 && frn < record_count) {
            if (scan.cancel.load(std::memory_order_relaxed)) return ES_ERR_CANCELLED;
            const uint64_t want = std::min(chunk_bytes, run_bytes);
            // Volume reads must be sector multiples; records are, and `want` is a multiple of them.
            if (run.lcn < 0) {
                std::memset(chunk.data, 0, static_cast<size_t>(want)); // sparse: no records here
            } else if (!read_at(volume.get(), static_cast<uint64_t>(run.lcn) * cluster + run_offset, chunk.data,
                                static_cast<DWORD>(want))) {
                return ES_ERR_IO;
            }
            for (uint64_t pos = 0; pos + record_size <= want && frn < record_count; pos += record_size, ++frn) {
                uint8_t* r = chunk.data + pos;
                if (ntfs::apply_fixups(r, record_size) && ntfs::parse_record(r, record_size, record))
                    index.add(static_cast<uint32_t>(frn), record);
            }
            scan.files.store(frn, std::memory_order_relaxed); // records read, shown as progress
            run_offset += want;
            run_bytes -= want;
        }
    }

    // Root: the volume root is record 5; any other folder is found by its file ID.
    uint32_t root_frn = ntfs::kRootFrn;
    if (!same_path(trim_trailing_backslash(root_path), trim_trailing_backslash(volume_path))) {
        UniqueHandle dir(CreateFileW(to_long_path(root_path).c_str(), FILE_READ_ATTRIBUTES,
                                     FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                                     FILE_FLAG_BACKUP_SEMANTICS, nullptr));
        BY_HANDLE_FILE_INFORMATION file_info{};
        if (!dir.valid() || !GetFileInformationByHandle(dir.get(), &file_info)) return status_from_win32(GetLastError());
        root_frn = static_cast<uint32_t>(((static_cast<uint64_t>(file_info.nFileIndexHigh) << 32) | file_info.nFileIndexLow) &
                                         ntfs::kFrnMask);
    }

    set_progress(scan, L"Building the folder tree…");
    uint64_t files = 0, dirs = 0, bytes = 0;
    const std::u16string_view root_name(reinterpret_cast<const char16_t*>(root_path.data()), root_path.size());
    if (!index.build_tree(root_frn, root_name, scan.store, scan.cancel, files, dirs, bytes))
        return scan.cancel.load() ? ES_ERR_CANCELLED : ES_ERR_NOT_FOUND;
    scan.files = files;
    scan.directories = dirs;
    scan.bytes = bytes;
    return ES_OK;
}

} // namespace emmascan::win
