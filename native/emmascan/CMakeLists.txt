add_library(emmascan SHARED
    include/emmascan/emmascan.h
    src/api.cpp
    src/node_store.cpp
    src/node_store.h
    src/ntfs/mft_parser.cpp
    src/ntfs/mft_parser.h
    src/scan_state.h
    src/time_util.h
)

if(WIN32)
    target_sources(emmascan PRIVATE
        src/win/mft_reader.cpp
        src/win/win_common.h
        src/win/win_platform.cpp
        src/win/win_walker.cpp
    )
    target_compile_definitions(emmascan PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
    target_link_libraries(emmascan PRIVATE ole32 shell32)
else()
    find_package(Threads REQUIRED)
    target_sources(emmascan PRIVATE src/posix/posix_platform.cpp)
    target_link_libraries(emmascan PRIVATE Threads::Threads)
    if(APPLE)
        enable_language(OBJCXX)
        target_sources(emmascan PRIVATE src/posix/trash_mac.mm)
        set_source_files_properties(src/posix/trash_mac.mm PROPERTIES COMPILE_OPTIONS "-fobjc-arc")
        target_link_libraries(emmascan PRIVATE "-framework Foundation")
    endif()
endif()

target_include_directories(emmascan PUBLIC include)
target_compile_definitions(emmascan PRIVATE EMMASCAN_BUILDING)
target_compile_features(emmascan PRIVATE cxx_std_20)
set_target_properties(emmascan PROPERTIES
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
)

if(MSVC)
    target_compile_options(emmascan PRIVATE /W4 /permissive- /utf-8)
else()
    target_compile_options(emmascan PRIVATE -Wall -Wextra -Wpedantic)
endif()

if(EMMASCAN_OUTPUT_DIR)
    # The generator expression stops multi-config generators from adding a Debug/Release subfolder.
    set_target_properties(emmascan PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY "${EMMASCAN_OUTPUT_DIR}$<0:>"
        LIBRARY_OUTPUT_DIRECTORY "${EMMASCAN_OUTPUT_DIR}$<0:>"
    )
endif()

if(EMMASCAN_BUILD_TESTS)
    add_subdirectory(tests)
endif()
