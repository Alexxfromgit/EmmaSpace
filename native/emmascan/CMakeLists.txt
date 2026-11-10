set(EMMASCAN_SOURCES
    include/emmascan/emmascan.h
    src/api.cpp
    src/ext4/ext4_parser.cpp
    src/ext4/ext4_parser.h
    src/node_store.cpp
    src/node_store.h
    src/ntfs/mft_parser.cpp
    src/ntfs/mft_parser.h
    src/scan_state.h
    src/time_util.h
    src/utf8.h
)

if(WIN32)
    list(APPEND EMMASCAN_SOURCES
        src/win/mft_reader.cpp
        src/win/win_common.h
        src/win/win_platform.cpp
        src/win/win_walker.cpp
    )
else()
    find_package(Threads REQUIRED)
    list(APPEND EMMASCAN_SOURCES src/posix/posix_platform.cpp)
    if(APPLE)
        enable_language(OBJCXX)
        list(APPEND EMMASCAN_SOURCES src/posix/storage_mac.cpp src/posix/trash_mac.mm)
    else()
        list(APPEND EMMASCAN_SOURCES src/posix/ext4_reader.h src/posix/ext4_reader_linux.cpp)
    endif()
endif()

# The engine is compiled once; the shared library and the Linux helper both link these objects.
add_library(emmascan_objects OBJECT ${EMMASCAN_SOURCES})
target_include_directories(emmascan_objects PUBLIC include)
target_compile_definitions(emmascan_objects PRIVATE EMMASCAN_BUILDING)
target_compile_features(emmascan_objects PUBLIC cxx_std_20)
set_target_properties(emmascan_objects PROPERTIES
    POSITION_INDEPENDENT_CODE ON
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
)
if(WIN32)
    target_compile_definitions(emmascan_objects PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
elseif(APPLE)
    set_source_files_properties(src/posix/trash_mac.mm PROPERTIES COMPILE_OPTIONS "-fobjc-arc")
endif()

function(emmascan_link target)
    target_link_libraries(${target} PRIVATE emmascan_objects)
    if(WIN32)
        target_link_libraries(${target} PRIVATE ole32 shell32)
    else()
        target_link_libraries(${target} PRIVATE Threads::Threads)
        if(APPLE)
            target_link_libraries(${target} PRIVATE "-framework Foundation" "-framework CoreFoundation" "-framework IOKit"
                                  "-framework DiskArbitration")
        endif()
    endif()
    if(EMMASCAN_OUTPUT_DIR)
        # The generator expression stops multi-config generators from adding a Debug/Release subfolder.
        set_target_properties(${target} PROPERTIES
            RUNTIME_OUTPUT_DIRECTORY "${EMMASCAN_OUTPUT_DIR}$<0:>"
            LIBRARY_OUTPUT_DIRECTORY "${EMMASCAN_OUTPUT_DIR}$<0:>"
        )
    endif()
endfunction()

function(emmascan_warnings target)
    if(MSVC)
        target_compile_options(${target} PRIVATE /W4 /permissive- /utf-8)
    else()
        target_compile_options(${target} PRIVATE -Wall -Wextra -Wpedantic)
    endif()
endfunction()
emmascan_warnings(emmascan_objects)

add_library(emmascan SHARED)
emmascan_link(emmascan)
target_include_directories(emmascan PUBLIC include)

# Linux: the fast scan reads the block device, which needs root. The app starts this helper through
# pkexec, so the user interface itself never runs as root. It holds the whole engine (no libemmascan.so,
# static C++ runtime), so it works from wherever the app copies it.
if(UNIX AND NOT APPLE)
    add_executable(emmascan-helper tools/emmascan_helper.cpp)
    emmascan_link(emmascan-helper)
    emmascan_warnings(emmascan-helper)
    target_include_directories(emmascan-helper PRIVATE src)
    target_link_options(emmascan-helper PRIVATE -static-libstdc++ -static-libgcc)
endif()

if(EMMASCAN_BUILD_TESTS)
    add_subdirectory(tests)
endif()
