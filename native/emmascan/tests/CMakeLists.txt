include(FetchContent)
FetchContent_Declare(Catch2
    GIT_REPOSITORY https://github.com/catchorg/Catch2.git
    GIT_TAG v3.16.1
    GIT_SHALLOW TRUE
)
FetchContent_MakeAvailable(Catch2)

# NodeStore is internal, so its sources are compiled straight into the test binary;
# the API tests go through the shared library like any consumer would.
add_executable(emmascan_tests
    test_ext4.cpp
    test_mft_parser.cpp
    test_node_store.cpp
    test_scan_api.cpp
    ../src/ext4/ext4_parser.cpp
    ../src/node_store.cpp
    ../src/ntfs/mft_parser.cpp
)
if(NOT WIN32)
    target_sources(emmascan_tests PRIVATE test_posix_entry.cpp)
endif()
target_include_directories(emmascan_tests PRIVATE ../src)
target_link_libraries(emmascan_tests PRIVATE emmascan Catch2::Catch2WithMain)
target_compile_features(emmascan_tests PRIVATE cxx_std_20)
if(MSVC)
    target_compile_options(emmascan_tests PRIVATE /utf-8)
endif()

if(WIN32)
    add_custom_command(TARGET emmascan_tests POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_RUNTIME_DLLS:emmascan_tests> $<TARGET_FILE_DIR:emmascan_tests>
        COMMAND_EXPAND_LISTS
    )
endif()

include(Catch)
catch_discover_tests(emmascan_tests)
