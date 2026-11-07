#include "emmascan/emmascan.h"
#include "scan_state.h"

#include <algorithm>
#include <chrono>
#include <cstring>
#include <memory>
#include <new>

namespace {

std::u16string_view to_view(const es_char* s) {
    if (!s) return {};
    size_t len = 0;
    while (s[len] != 0) ++len;
    return {reinterpret_cast<const char16_t*>(s), len};
}

void run_scan(es_scan* scan) {
    const auto start = std::chrono::steady_clock::now();
    es_status status;
    try {
        status = emmascan::platform::scan(*scan);
        if (status == ES_OK) {
            if (scan->cancel.load()) {
                status = ES_ERR_CANCELLED;
            } else {
                scan->phase = ES_PHASE_FINALIZING;
                scan->store.finalize();
            }
        }
    } catch (const std::bad_alloc&) {
        status = ES_ERR_NO_MEMORY;
    } catch (...) {
        status = ES_ERR_INTERNAL;
    }
    const auto elapsed = std::chrono::steady_clock::now() - start;

    std::lock_guard lock(scan->done_mutex);
    scan->elapsed_ms = static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(elapsed).count());
    scan->status = status;
    scan->phase = ES_PHASE_DONE;
    scan->done = true;
    scan->done_cv.notify_all();
}

} // namespace

extern "C" {

ES_API uint32_t ES_CALL es_get_version(void) { return ES_ABI_VERSION; }

ES_API int32_t ES_CALL es_is_elevated(void) { return emmascan::platform::is_elevated() ? 1 : 0; }

ES_API es_status ES_CALL es_scan_start(const es_scan_options* options, es_scan** out_scan) {
    if (!options || !out_scan || options->struct_size < sizeof(es_scan_options) || !options->root_path)
        return ES_ERR_INVALID_ARG;
    *out_scan = nullptr;
    auto root = to_view(options->root_path);
    if (root.empty()) return ES_ERR_INVALID_ARG;

    // Nothing may throw across the C ABI: set-up allocations are inside the handler too.
    try {
        auto scan = std::make_unique<es_scan>();
        scan->root.assign(root);
        scan->requested_mode = options->mode;
        scan->thread_count = options->thread_count;
        scan->flags = options->flags;
        scan->thread = std::thread(run_scan, scan.get());
        *out_scan = scan.release();
        return ES_OK;
    } catch (const std::bad_alloc&) {
        return ES_ERR_NO_MEMORY;
    } catch (...) {
        return ES_ERR_INTERNAL;
    }
}

ES_API es_status ES_CALL es_scan_get_progress(es_scan* scan, es_progress* out, es_char* path_buffer,
                                              uint32_t path_buffer_length) {
    if (!scan || !out) return ES_ERR_INVALID_ARG;
    out->files = scan->files.load(std::memory_order_relaxed);
    out->directories = scan->directories.load(std::memory_order_relaxed);
    out->bytes = scan->bytes.load(std::memory_order_relaxed);
    out->errors = scan->errors.load(std::memory_order_relaxed);
    out->phase = scan->phase.load();
    out->mode = scan->mode_used.load();
    out->path_length = 0;
    if (path_buffer && path_buffer_length > 0) {
        std::lock_guard lock(scan->path_mutex);
        const auto n = static_cast<uint32_t>(std::min<size_t>(scan->current_path.size(), path_buffer_length - 1));
        std::memcpy(path_buffer, scan->current_path.data(), n * sizeof(es_char));
        path_buffer[n] = 0;
        out->path_length = n;
    }
    return ES_OK;
}

ES_API es_status ES_CALL es_scan_wait(es_scan* scan, uint32_t timeout_ms) {
    if (!scan) return ES_ERR_INVALID_ARG;
    std::unique_lock lock(scan->done_mutex);
    if (!scan->done_cv.wait_for(lock, std::chrono::milliseconds(timeout_ms), [scan] { return scan->done; }))
        return ES_ERR_TIMEOUT;
    return scan->status;
}

ES_API void ES_CALL es_scan_cancel(es_scan* scan) {
    if (scan) scan->cancel = true;
}

ES_API es_status ES_CALL es_scan_get_result(es_scan* scan, es_result* out) {
    if (!scan || !out) return ES_ERR_INVALID_ARG;
    std::lock_guard lock(scan->done_mutex);
    if (!scan->done) return ES_ERR_RUNNING;
    if (scan->status != ES_OK) return scan->status;
    auto& nodes = scan->store.nodes();
    auto& names = scan->store.names();
    out->nodes = nodes.data();
    out->node_count = static_cast<uint32_t>(nodes.size());
    out->names = reinterpret_cast<const es_char*>(names.data());
    out->names_length = names.size();
    out->mode = scan->mode_used.load();
    out->errors = scan->errors.load();
    out->reserved = 0;
    out->elapsed_ms = scan->elapsed_ms;
    return ES_OK;
}

ES_API void ES_CALL es_scan_free(es_scan* scan) {
    if (!scan) return;
    scan->cancel = true;
    if (scan->thread.joinable()) scan->thread.join();
    delete scan;
}

ES_API es_status ES_CALL es_move_to_trash(const es_char* path) {
    auto view = to_view(path);
    if (view.empty()) return ES_ERR_INVALID_ARG;
    try {
        return emmascan::platform::move_to_trash(view);
    } catch (...) {
        return ES_ERR_INTERNAL;
    }
}

} // extern "C"
