#include "../scan_state.h"
#include "win_common.h"

#include <objbase.h>
#include <shobjidl.h>
#include <shellapi.h>

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

} // namespace emmascan::platform
