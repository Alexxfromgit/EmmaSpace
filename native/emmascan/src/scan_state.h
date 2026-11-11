#pragma once

#include "emmascan/emmascan.h"
#include "node_store.h"

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <mutex>
#include <string>
#include <string_view>
#include <thread>

// Definition of the opaque handle declared in emmascan.h.
struct es_scan {
    // Options
    std::u16string root;
    uint32_t requested_mode = ES_MODE_AUTO;
    uint32_t thread_count = 0;
    uint32_t flags = 0;

    // Live progress, readable from any thread
    std::atomic<bool> cancel{false};
    std::atomic<uint64_t> files{0};
    std::atomic<uint64_t> directories{0};
    std::atomic<uint64_t> bytes{0};
    std::atomic<uint32_t> errors{0};
    std::atomic<uint32_t> phase{ES_PHASE_STARTING};
    std::atomic<uint32_t> mode_used{ES_MODE_WALKER};

    std::mutex path_mutex;
    std::u16string current_path;

    void set_current_path(std::u16string_view path) {
        std::lock_guard lock(path_mutex);
        current_path.assign(path);
    }

    // Completion
    std::mutex done_mutex;
    std::condition_variable done_cv;
    bool done = false;
    es_status status = ES_ERR_RUNNING;
    uint64_t elapsed_ms = 0;

    emmascan::NodeStore store;
    std::thread thread;
};

namespace emmascan::platform {

// Runs a complete scan synchronously on the calling thread, filling scan.store.
// Implementations must honour scan.cancel and keep the progress counters current.
es_status scan(es_scan& scan);

bool is_elevated();

es_status move_to_trash(std::u16string_view path);

// es_storage_kind of the volume holding path; ES_STORAGE_UNKNOWN when it cannot be told. device is set
// to a value identifying that file system (0 when unknown).
uint32_t storage_kind(std::u16string_view path, uint64_t& device);

// Fills stamp from an open file handle (HANDLE on Windows, file descriptor elsewhere).
es_status file_stamp(intptr_t handle, es_file_stamp& stamp);

} // namespace emmascan::platform
