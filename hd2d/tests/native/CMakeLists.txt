cmake_minimum_required(VERSION 3.20)
project(hd2d_scanout_tests C)
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
include(CTest)
get_filename_component(ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../.." ABSOLUTE)
set(RUNNER "${ROOT}/snesrecomp/runner/src")
set(BRIDGE "${ROOT}/hd2d/bridge")
if(NOT CMAKE_SYSTEM_NAME STREQUAL "Linux")
    message(FATAL_ERROR "This ROM-free title-link harness currently requires Linux (section GC).")
endif()
find_package(Git REQUIRED)
execute_process(COMMAND ${GIT_EXECUTABLE} rev-parse HEAD
    WORKING_DIRECTORY "${ROOT}/snesrecomp" OUTPUT_VARIABLE ENGINE
    OUTPUT_STRIP_TRAILING_WHITESPACE COMMAND_ERROR_IS_FATAL ANY)
if(NOT ENGINE STREQUAL "93fc677a2a9b688751d575ed1232343d83d3d2c0")
    message(FATAL_ERROR "Runtime v1 parity is qualified only against the Sprint 0 PPU pin.")
endif()
option(HD2D_SANITIZE "Address/undefined behavior sanitizers" OFF)
if(HD2D_SANITIZE)
    add_compile_options(-fsanitize=address,undefined -fno-omit-frame-pointer)
    add_link_options(-fsanitize=address,undefined)
    # Pinned PPU's packed bitplane expansion uses signed shifts (ppu.c:1300).
    # Reproduces with capture compiled out. Keep its source/pin unchanged;
    # exempt only shift-base in that file, not in any HD-2D code.
    set_source_files_properties(${RUNNER}/snes/ppu.c PROPERTIES
        COMPILE_OPTIONS "-fno-sanitize=shift-base")
endif()
if(NOT MSVC)
    add_compile_options(-Wall -Wextra -ffunction-sections -fdata-sections)
    add_link_options(-Wl,--gc-sections)
endif()
add_library(hd2d_test_ppu STATIC
    ${RUNNER}/snes/ppu.c ${RUNNER}/snes/ppu_legacy.c ${RUNNER}/sha256.c
    ${BRIDGE}/scanout.c ${BRIDGE}/replay.c ppu_stubs.c)
target_include_directories(hd2d_test_ppu PUBLIC ${RUNNER} ${ROOT}/src ${BRIDGE})
target_compile_definitions(hd2d_test_ppu PUBLIC SNESRECOMP_REVERSE_DEBUG=0)
if(NOT MSVC)
    target_link_libraries(hd2d_test_ppu PUBLIC m)
endif()
# Link the actual title source: unused ROM/CPU sections are discarded. HDMA is
# deterministic synthetic input here; its real implementation is not replaced
# in the product. Both enabled and compiled-out title loops are exercised.
foreach(mode IN ITEMS on off adapter)
    add_executable(scanout_${mode} scanout_test.c ${ROOT}/src/x2_rtl.c)
    target_link_libraries(scanout_${mode} PRIVATE hd2d_test_ppu)
    if(NOT mode STREQUAL "off")
        target_compile_definitions(scanout_${mode} PRIVATE HD2D_SCANOUT_CAPTURE=1)
    endif()
    if(mode STREQUAL "adapter")
        target_sources(scanout_${mode} PRIVATE ${BRIDGE}/x2_adapter.c)
        target_compile_definitions(scanout_${mode} PRIVATE TEST_ADAPTER=1)
    else()
        add_test(NAME scanout_${mode} COMMAND scanout_${mode})
    endif()
endforeach()
add_executable(scanout_replay replay_main.c)
target_link_libraries(scanout_replay PRIVATE hd2d_test_ppu)
find_package(Python3 COMPONENTS Interpreter REQUIRED)
add_test(NAME scanout_files COMMAND ${Python3_EXECUTABLE}
    ${CMAKE_CURRENT_SOURCE_DIR}/test_files.py $<TARGET_FILE:scanout_on>
    $<TARGET_FILE:scanout_off> $<TARGET_FILE:scanout_adapter> $<TARGET_FILE:scanout_replay>)
