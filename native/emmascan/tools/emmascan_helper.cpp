// emmascan-helper: runs one scan as root on behalf of Emma Space (Linux).
//
// The app starts it through pkexec so the user interface never runs as root:
//
//     emmascan-helper scan <absolute folder path>
//
// Nothing else is accepted. The scan uses Auto mode, so a whole ext2/3/4 file system is read straight from
// its block device and anything else is walked folder by folder (as root, so nothing is denied).
//
// Output on stdout, little-endian (EmmaSpace.Core/Scanning/HelperProtocol.cs reads it):
//   header:   "EMSH"  u32 version (1)  u32 sizeof(es_node) (64)
//   progress: u8 1  u64 files  u64 directories  u64 bytes  u32 errors  u32 phase  u32 mode
//             u32 path_length  path (UTF-16)                                      every 100 ms
//   result:   u8 2  u32 status  u32 mode  u32 errors  u64 elapsed_ms  u32 node_count  u64 names_length
//             nodes (es_node[node_count])  names (UTF-16[names_length])           once, last
// Any byte on stdin, or stdin closing (the app quit), cancels the scan.

#include "emmascan/emmascan.h"
#include "utf8.h"

#include <unistd.h>

#include <cerrno>
#include <csignal>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
#include <type_traits>
#include <vector>

namespace {

constexpr uint32_t kProtocolVersion = 1;
constexpr uint8_t kFrameProgress = 1;
constexpr uint8_t kFrameResult = 2;
constexpr uint32_t kPathBuffer = 1024;

class Writer {
public:
    template <class T>
    void put(const T& value) {
        static_assert(std::is_trivially_copyable_v<T>);
        const auto* p = reinterpret_cast<const uint8_t*>(&value);
        buf_.insert(buf_.end(), p, p + sizeof(T));
    }
    void put_bytes(const void* data, size_t length) {
        const auto* p = static_cast<const uint8_t*>(data);
        buf_.insert(buf_.end(), p, p + length);
    }
    // False once the app has gone away (EPIPE).
    bool flush() {
        const uint8_t* p = buf_.data();
        size_t left = buf_.size();
        while (left > 0) {
            const ssize_t n = write(STDOUT_FILENO, p, left);
            if (n < 0 && errno == EINTR) continue;
            if (n <= 0) return false;
            p += n;
            left -= static_cast<size_t>(n);
        }
        buf_.clear();
        return true;
    }

private:
    std::vector<uint8_t> buf_;
};

// Writes a large block straight through, without copying it into the buffer first.
bool write_all(const void* data, size_t length) {
    const auto* p = static_cast<const uint8_t*>(data);
    while (length > 0) {
        const ssize_t n = write(STDOUT_FILENO, p, length);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) return false;
        p += n;
        length -= static_cast<size_t>(n);
    }
    return true;
}

bool send_result(Writer& w, es_status status, const es_result* r) {
    w.put(kFrameResult);
    w.put(static_cast<uint32_t>(status));
    w.put(r ? r->mode : uint32_t{0});
    w.put(r ? r->errors : uint32_t{0});
    w.put(r ? r->elapsed_ms : uint64_t{0});
    w.put(r ? r->node_count : uint32_t{0});
    w.put(r ? r->names_length : uint64_t{0});
    if (!w.flush()) return false;
    if (!r) return true;
    return write_all(r->nodes, size_t{r->node_count} * sizeof(es_node)) &&
           write_all(r->names, static_cast<size_t>(r->names_length) * sizeof(es_char));
}

int usage() {
    std::fputs("usage: emmascan-helper scan <absolute folder path>\n"
               "Runs one Emma Space scan and writes the result to stdout. Started by Emma Space.\n"
               "Keep stdin open while it runs: any input, or end of input, cancels the scan\n"
               "(for example: sleep infinity | emmascan-helper scan / > out.bin).\n",
               stderr);
    return 2;
}

} // namespace

int main(int argc, char** argv) {
    if (argc != 3 || std::strcmp(argv[1], "scan") != 0 || argv[2][0] != '/') return usage();
    if (isatty(STDOUT_FILENO)) {
        std::fputs("emmascan-helper writes binary data; redirect stdout.\n", stderr);
        return 2;
    }
    std::signal(SIGPIPE, SIG_IGN); // a vanished reader shows up as a failed write instead

    Writer w;
    w.put_bytes("EMSH", 4);
    w.put(kProtocolVersion);
    w.put(static_cast<uint32_t>(sizeof(es_node)));
    if (!w.flush()) return 1;

    std::u16string path = emmascan::utf8_to_utf16(argv[2]);
    es_scan_options options{};
    options.struct_size = sizeof(options);
    options.mode = ES_MODE_AUTO;
    options.root_path = reinterpret_cast<const es_char*>(path.c_str());
    es_scan* scan = nullptr;
    const es_status started = es_scan_start(&options, &scan);
    if (started != ES_OK) return send_result(w, started, nullptr) ? 0 : 1;

    // Blocks until the app writes a byte or closes the pipe; then the scan stops. Detached: it may still be
    // blocked in read() when main returns, which is fine.
    std::thread([scan] {
        char c;
        while (read(STDIN_FILENO, &c, 1) < 0 && errno == EINTR) {
        }
        es_scan_cancel(scan);
    }).detach();

    std::vector<es_char> buffer(kPathBuffer);
    es_status status;
    bool connected = true;
    while ((status = es_scan_wait(scan, 100)) == ES_ERR_TIMEOUT) {
        es_progress p{};
        if (es_scan_get_progress(scan, &p, buffer.data(), kPathBuffer) != ES_OK) continue;
        w.put(kFrameProgress);
        w.put(p.files);
        w.put(p.directories);
        w.put(p.bytes);
        w.put(p.errors);
        w.put(p.phase);
        w.put(p.mode);
        w.put(p.path_length);
        w.put_bytes(buffer.data(), size_t{p.path_length} * sizeof(es_char));
        if (!w.flush()) {
            connected = false;
            es_scan_cancel(scan);
        }
    }

    int exit_code = 0;
    if (connected) {
        es_result result{};
        if (status == ES_OK) status = es_scan_get_result(scan, &result);
        if (!send_result(w, status, status == ES_OK ? &result : nullptr)) exit_code = 1;
    } else {
        exit_code = 1;
    }
    // No es_scan_free(): the stdin thread may still call es_scan_cancel(scan) when the app closes the pipe, so the
    // handle must stay valid until the process exits, which happens right now and releases everything anyway.
    return exit_code;
}
