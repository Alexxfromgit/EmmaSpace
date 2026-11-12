#include "../scan_state.h"
#include "win_common.h"

#include <objbase.h>
#include <shobjidl.h>
#include <shellapi.h>
#include <winioctl.h>

#include <cstddef>
#include <cstring>
#include <thread>

namespace emmascan::platform {

es_status scan(es_scan& scan) {
    const std::wstring root =
        win::normalize_root(std::wstring_view(reinterpret_cast<const wchar_t*>(scan.root.data()), scan.root.size()));
    switch (scan.requested_mode) {
    case ES_MODE_MFT:
        return win::mft_scan(scan, root);
    case ES_MODE_WALKER:
        return win::walk(scan, root);
    case ES_MODE_EXT4:
        return ES_ERR_NOT_SUPPORTED;
    default:
        // Auto: the MFT is far faster for a whole volume but needs elevation and NTFS. It always
        // reads the entire table, so for a subfolder walking that folder is quicker.
        if (is_elevated() && win::is_volume_root(root)) {
            const es_status status = win::mft_scan(scan, root);
            if (status == ES_OK || status == ES_ERR_CANCELLED || scan.cancel.load()) return status;
            scan.store = NodeStore{};
            scan.files = scan.directories = scan.bytes = 0;
        }
        return win::walk(scan, root);
    }
}

bool is_elevated() {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
    TOKEN_ELEVATION elevation{};
    DWORD size = sizeof(elevation);
    const BOOL ok = GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &size);
    CloseHandle(token);
    return ok && elevation.TokenIsElevated != 0;
}

namespace {

es_status trash_on_sta(const std::wstring& path) {
    IShellItem* item = nullptr;
    HRESULT hr = SHCreateItemFromParsingName(path.c_str(), nullptr, IID_PPV_ARGS(&item));
    if (FAILED(hr)) return hr == HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) ? ES_ERR_NOT_FOUND : ES_ERR_IO;

    IFileOperation* op = nullptr;
    hr = CoCreateInstance(CLSID_FileOperation, nullptr, CLSCTX_ALL, IID_PPV_ARGS(&op));
    if (FAILED(hr)) {
        item->Release();
        return ES_ERR_INTERNAL;
    }

    // FOF_WANTNUKEWARNING makes the shell ask before deleting permanently (e.g. the item is
    // too large for the Recycle Bin), so nothing is ever destroyed silently.
    op->SetOperationFlags(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_NOERRORUI |
                          FOFX_RECYCLEONDELETE);
    hr = op->DeleteItem(item, nullptr);
    if (SUCCEEDED(hr)) hr = op->PerformOperations();
    BOOL aborted = FALSE;
    if (SUCCEEDED(hr)) op->GetAnyOperationsAborted(&aborted);
    op->Release();
    item->Release();

    if (FAILED(hr)) return hr == HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED) ? ES_ERR_ACCESS_DENIED : ES_ERR_IO;
    return aborted ? ES_ERR_CANCELLED : ES_OK;
}

} // namespace

es_status move_to_trash(std::u16string_view path) {
    std::wstring p(reinterpret_cast<const wchar_t*>(path.data()), path.size());
    for (auto& c : p)
        if (c == L'/') c = L'\\';

    // IFileOperation wants an STA; the caller's thread may already be MTA, so use our own.
    es_status status = ES_ERR_INTERNAL;
    std::thread sta([&] {
        const HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
        status = trash_on_sta(p);
        if (SUCCEEDED(init)) CoUninitialize();
    });
    sta.join();
    return status;
}

namespace {

// Nanoseconds since the Unix epoch from a count of 100 ns steps since 1601; 0 (no such time) stays 0.
int64_t filetime_to_unix_ns(int64_t filetime) { return filetime == 0 ? 0 : (filetime - 116444736000000000LL) * 100; }

} // namespace

// Windows frees nothing by itself when space runs low, so DriveInfo's free space is already the whole answer.
es_status volume_space(std::u16string_view, es_volume_space&) { return ES_ERR_NOT_SUPPORTED; }

uint32_t storage_kind(std::u16string_view path, uint64_t& device) {
    device = 0;
    std::wstring input(reinterpret_cast<const wchar_t*>(path.data()), path.size());
    // GetVolumePathName reads at most MAX_PATH characters. A parent folder is on the same volume (one
    // mounted in a folder that deep is not realistic), so a long path is asked about through one.
    while (input.size() >= MAX_PATH) {
        const auto separator = input.find_last_of(L"\\/");
        if (separator == std::wstring::npos || separator < 3) return ES_STORAGE_UNKNOWN;
        input.resize(separator);
    }
    wchar_t mount_point[MAX_PATH + 1];
    if (!GetVolumePathNameW(input.c_str(), mount_point, MAX_PATH + 1)) return ES_STORAGE_UNKNOWN;
    DWORD serial = 0;
    if (GetVolumeInformationW(mount_point, nullptr, 0, &serial, nullptr, nullptr, nullptr, 0)) device = serial;
    switch (GetDriveTypeW(mount_point)) {
    case DRIVE_REMOTE:
        return ES_STORAGE_NETWORK;
    case DRIVE_RAMDISK:
        return ES_STORAGE_SSD;
    case DRIVE_FIXED:
    case DRIVE_REMOVABLE:
        break;
    default:
        return ES_STORAGE_UNKNOWN;
    }

    // "\\?\Volume{guid}\" names the volume whether it has a drive letter or is mounted in a folder;
    // without the trailing backslash it opens the volume device itself.
    wchar_t volume[64];
    if (!GetVolumeNameForVolumeMountPointW(mount_point, volume, 64)) return ES_STORAGE_UNKNOWN;
    std::wstring device_path(volume);
    if (!device_path.empty() && device_path.back() == L'\\') device_path.pop_back();

    // No access rights asked for: enough for this query, works without elevation and reads nothing.
    HANDLE handle = CreateFileW(device_path.c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                                OPEN_EXISTING, 0, nullptr);
    if (handle == INVALID_HANDLE_VALUE) return ES_STORAGE_UNKNOWN;
    STORAGE_PROPERTY_QUERY query{};
    query.PropertyId = StorageDeviceSeekPenaltyProperty;
    query.QueryType = PropertyStandardQuery;
    DEVICE_SEEK_PENALTY_DESCRIPTOR seek{};
    DWORD returned = 0;
    // Fails for volumes spanning several disks (e.g. Storage Spaces), which are then "unknown".
    const BOOL ok = DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, &query, sizeof(query), &seek, sizeof(seek),
                                    &returned, nullptr);
    CloseHandle(handle);
    if (!ok || returned < sizeof(seek)) return ES_STORAGE_UNKNOWN;
    return seek.IncursSeekPenalty ? ES_STORAGE_HDD : ES_STORAGE_SSD;
}

es_status file_stamp(intptr_t handle_value, es_file_stamp& stamp) {
    const HANDLE handle = reinterpret_cast<HANDLE>(handle_value);
    if (handle == nullptr || handle == INVALID_HANDLE_VALUE) return ES_ERR_INVALID_ARG;
    FILE_BASIC_INFO basic{};
    FILE_STANDARD_INFO standard{};
    if (!GetFileInformationByHandleEx(handle, FileBasicInfo, &basic, sizeof(basic)) ||
        !GetFileInformationByHandleEx(handle, FileStandardInfo, &standard, sizeof(standard)))
        return win::status_from_win32(GetLastError());

    FILE_ID_INFO id{};
    if (GetFileInformationByHandleEx(handle, FileIdInfo, &id, sizeof(id))) {
        stamp.volume = id.VolumeSerialNumber;
        std::memcpy(&stamp.file_id_low, id.FileId.Identifier, sizeof(stamp.file_id_low));
        std::memcpy(&stamp.file_id_high, id.FileId.Identifier + sizeof(stamp.file_id_low), sizeof(stamp.file_id_high));
    } else {
        // File systems without 128-bit IDs (e.g. FAT) still have the classic 64-bit index.
        BY_HANDLE_FILE_INFORMATION info{};
        if (!GetFileInformationByHandle(handle, &info)) return win::status_from_win32(GetLastError());
        stamp.volume = info.dwVolumeSerialNumber;
        stamp.file_id_low = (static_cast<uint64_t>(info.nFileIndexHigh) << 32) | info.nFileIndexLow;
    }
    stamp.size = static_cast<uint64_t>(standard.EndOfFile.QuadPart);
    stamp.mtime_ns = filetime_to_unix_ns(basic.LastWriteTime.QuadPart);
    stamp.ctime_ns = filetime_to_unix_ns(basic.ChangeTime.QuadPart);

    // Programs can set every time back, ChangeTime included, but not the change journal's USN.
    READ_FILE_USN_DATA input{};
    input.MinMajorVersion = 2;
    input.MaxMajorVersion = 3;
    alignas(8) unsigned char record[sizeof(USN_RECORD_V3) + 2 * MAX_PATH * sizeof(wchar_t)];
    DWORD returned = 0;
    if (DeviceIoControl(handle, FSCTL_READ_FILE_USN_DATA, &input, sizeof(input), record, sizeof(record), &returned,
                        nullptr) &&
        returned >= sizeof(USN_RECORD_COMMON_HEADER)) {
        const auto version = reinterpret_cast<const USN_RECORD_COMMON_HEADER*>(record)->MajorVersion;
        if (version == 2 && returned >= offsetof(USN_RECORD_V2, Usn) + sizeof(USN))
            stamp.usn = static_cast<uint64_t>(reinterpret_cast<const USN_RECORD_V2*>(record)->Usn);
        else if (version == 3 && returned >= offsetof(USN_RECORD_V3, Usn) + sizeof(USN))
            stamp.usn = static_cast<uint64_t>(reinterpret_cast<const USN_RECORD_V3*>(record)->Usn);
    }
    if (stamp.usn != 0 && stamp.ctime_ns != 0) stamp.flags |= ES_STAMP_TRUSTED;
    return ES_OK;
}

} // namespace emmascan::platform
