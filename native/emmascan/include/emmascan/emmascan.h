/*
 * emmascan — native disk scanning engine for Emma Space.
 *
 * This header is the only public interface of the library. It is a plain C ABI so
 * it can be consumed from .NET (LibraryImport) or any other language.
 *
 * Model: a scan produces a flat tree. Node 0 is the root; every other node points at
 * its parent, first child and next sibling by index. Names live in one UTF-16 pool
 * and nodes reference them by offset/length. Directory sizes are subtree totals and
 * siblings are ordered by size, largest first.
 */
#ifndef EMMASCAN_H
#define EMMASCAN_H

#include <stdint.h>

#if defined(_WIN32)
#  if defined(EMMASCAN_BUILDING)
#    define ES_API __declspec(dllexport)
#  else
#    define ES_API __declspec(dllimport)
#  endif
#  define ES_CALL __cdecl
#else
#  define ES_API __attribute__((visibility("default")))
#  define ES_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define ES_ABI_VERSION 1u
#define ES_NONE 0xFFFFFFFFu

typedef uint16_t es_char; /* UTF-16 code unit */

typedef enum es_status {
    ES_OK = 0,
    ES_ERR_INVALID_ARG = 1,
    ES_ERR_NOT_FOUND = 2,
    ES_ERR_ACCESS_DENIED = 3,
    ES_ERR_CANCELLED = 4,
    ES_ERR_NOT_SUPPORTED = 5,
    ES_ERR_IO = 6,
    ES_ERR_RUNNING = 7,
    ES_ERR_TIMEOUT = 8,
    ES_ERR_NO_MEMORY = 9,
    ES_ERR_INTERNAL = 99
} es_status;

typedef enum es_scan_mode {
    ES_MODE_AUTO = 0,   /* fastest available method */
    ES_MODE_WALKER = 1, /* directory enumeration, works without elevation */
    ES_MODE_MFT = 2     /* NTFS master file table, requires elevation */
} es_scan_mode;

typedef enum es_scan_phase {
    ES_PHASE_STARTING = 0,
    ES_PHASE_ENUMERATING = 1,
    ES_PHASE_FINALIZING = 2,
    ES_PHASE_DONE = 3
} es_scan_phase;

/* es_scan_options.flags */
#define ES_SCAN_FOLLOW_REPARSE 0x1u

/* es_node.flags */
#define ES_NODE_DIRECTORY 0x01u
#define ES_NODE_REPARSE   0x02u /* junction / symlink / mount point, not descended */
#define ES_NODE_ERROR     0x04u /* could not be read (e.g. access denied) */
#define ES_NODE_HARDLINK  0x08u /* additional hard link, size not counted again */
#define ES_NODE_DELETED   0x10u /* reserved for consumers that remove nodes in place */
#define ES_NODE_CLOUD     0x20u /* cloud placeholder (e.g. OneDrive): size is the locally stored part only;
                                   on a folder the walker did not descend, to avoid a network fetch */

typedef struct es_scan_options {
    uint32_t struct_size;     /* sizeof(es_scan_options) */
    uint32_t mode;            /* es_scan_mode */
    uint32_t thread_count;    /* 0 = automatic */
    uint32_t flags;           /* ES_SCAN_* */
    const es_char* root_path; /* null-terminated UTF-16 */
} es_scan_options;

typedef struct es_node {
    uint64_t size;          /* logical bytes; subtree total for directories */
    uint64_t allocated;     /* bytes on disk; subtree total for directories */
    int64_t mtime;          /* last write, Unix epoch milliseconds */
    int64_t atime;          /* last access, Unix epoch milliseconds */
    uint32_t parent;        /* ES_NONE for the root */
    uint32_t first_child;   /* ES_NONE if none */
    uint32_t next_sibling;  /* ES_NONE if none */
    uint32_t name_offset;   /* into the name pool, in es_char units */
    uint32_t file_count;    /* files in subtree (1 for a file) */
    uint32_t dir_count;     /* directories in subtree, excluding this node */
    uint32_t attributes;    /* platform file attributes */
    uint16_t name_length;   /* in es_char units */
    uint16_t flags;         /* ES_NODE_* */
} es_node;                  /* 64 bytes */

typedef struct es_progress {
    uint64_t files;
    uint64_t directories;
    uint64_t bytes;
    uint32_t errors;
    uint32_t phase;         /* es_scan_phase */
    uint32_t mode;          /* es_scan_mode actually used */
    uint32_t path_length;   /* length of the current path copied into the caller's buffer */
} es_progress;

typedef struct es_result {
    const es_node* nodes;
    const es_char* names;
    uint64_t names_length;
    uint32_t node_count;
    uint32_t mode;          /* es_scan_mode actually used */
    uint32_t errors;
    uint32_t reserved;
    uint64_t elapsed_ms;
} es_result;

typedef struct es_scan es_scan; /* opaque */

ES_API uint32_t ES_CALL es_get_version(void);

/* Returns 1 if the process is elevated (administrator / root). */
ES_API int32_t ES_CALL es_is_elevated(void);

/* Starts an asynchronous scan. The handle must be released with es_scan_free. */
ES_API es_status ES_CALL es_scan_start(const es_scan_options* options, es_scan** out_scan);

/* Copies a progress snapshot. path_buffer (optional) receives the directory currently being read. */
ES_API es_status ES_CALL es_scan_get_progress(es_scan* scan, es_progress* out_progress,
                                              es_char* path_buffer, uint32_t path_buffer_length);

/* Waits for the scan to finish. Returns ES_ERR_TIMEOUT if still running after timeout_ms. */
ES_API es_status ES_CALL es_scan_wait(es_scan* scan, uint32_t timeout_ms);

/* Requests cancellation; es_scan_wait then returns ES_ERR_CANCELLED. */
ES_API void ES_CALL es_scan_cancel(es_scan* scan);

/* Returns the result of a finished scan. Pointers stay valid until es_scan_free.
   Node memory is writable by the caller (e.g. to apply in-place deletions). */
ES_API es_status ES_CALL es_scan_get_result(es_scan* scan, es_result* out_result);

/* Cancels if needed, waits, and frees all memory owned by the scan. */
ES_API void ES_CALL es_scan_free(es_scan* scan);

/* Moves a file or directory to the Recycle Bin / Trash. */
ES_API es_status ES_CALL es_move_to_trash(const es_char* path);

#ifdef __cplusplus
}
#endif

#endif /* EMMASCAN_H */
