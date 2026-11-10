#pragma once
// Linux fast scan: reads ext2/3/4 metadata straight from the block device (needs root).

#include "../scan_state.h"

#include <string>

namespace emmascan::platform {

// Scans `root` (an absolute folder path) through ext4::Ext4Index. ES_ERR_NOT_SUPPORTED when the folder is
// not on an ext2/3/4 file system this reader handles, ES_ERR_ACCESS_DENIED without root.
es_status ext4_scan(es_scan& scan, const std::string& root);

// True if `root` is where an ext2/3/4 file system is mounted (Auto mode only reads whole file systems).
bool is_ext4_mount_root(const std::string& root);

} // namespace emmascan::platform
