cmake_minimum_required(VERSION 3.16)
project(stb_oracle C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

# Pinned upstream commits.
# A pin moves only in its own PR, together with the header SHA-256 hashes below.
set(STB_COMMIT "9600522de53973336fa033a289858a4354cbb04c")
set(STB_IMAGE_RESIZE_COMMIT "703100c5f8a0b75b4861e401db567f1d82d545b8")

set(STB_SOURCE_DIR "" CACHE PATH "Optional pre-fetched nothings/stb source directory")
set(STB_IMAGE_RESIZE_SOURCE_DIR "" CACHE PATH "Optional pre-fetched nothings/stb_image_resize source directory")

include(FetchContent)

if(NOT STB_SOURCE_DIR)
    FetchContent_Declare(
        stb_upstream
        URL "https://github.com/nothings/stb/archive/${STB_COMMIT}.tar.gz"
        DOWNLOAD_EXTRACT_TIMESTAMP TRUE
    )
    FetchContent_MakeAvailable(stb_upstream)
    set(STB_INCLUDE_DIR "${stb_upstream_SOURCE_DIR}")
else()
    set(STB_INCLUDE_DIR "${STB_SOURCE_DIR}")
endif()

if(NOT STB_IMAGE_RESIZE_SOURCE_DIR)
    FetchContent_Declare(
        stb_image_resize_upstream
        URL "https://github.com/nothings/stb_image_resize/archive/${STB_IMAGE_RESIZE_COMMIT}.tar.gz"
        DOWNLOAD_EXTRACT_TIMESTAMP TRUE
    )
    FetchContent_MakeAvailable(stb_image_resize_upstream)
    set(STB_IMAGE_RESIZE_INCLUDE_DIR "${stb_image_resize_upstream_SOURCE_DIR}")
else()
    set(STB_IMAGE_RESIZE_INCLUDE_DIR "${STB_IMAGE_RESIZE_SOURCE_DIR}")
endif()

function(stb_verify_header file_path expected_sha256)
    if(NOT EXISTS "${file_path}")
        message(FATAL_ERROR "Expected stb header not found: ${file_path}")
    endif()
    file(SHA256 "${file_path}" actual_sha256)
    if(NOT "${actual_sha256}" STREQUAL "${expected_sha256}")
        message(FATAL_ERROR
            "SHA-256 mismatch for ${file_path}\n"
            "  Expected: ${expected_sha256}\n"
            "  Actual:   ${actual_sha256}")
    endif()
endfunction()

# Verify every pinned header byte-for-byte.
stb_verify_header("${STB_INCLUDE_DIR}/stb_image.h"             "deb4f18c9ca21b751a1323019031c94c0bf5ca1d2929ef5913cc56f00aab6e91")
stb_verify_header("${STB_INCLUDE_DIR}/stb_image_write.h"       "a8e7f77f0ccfdc9bac09a71268b131931ff81aafca82c393aeb6aaba10d9e7ca")
stb_verify_header("${STB_INCLUDE_DIR}/stb_truetype.h"          "428cfe93520f2aed5e413a220b0db5bba5b17e2ebe5a64e4a8efa9eeeb67ec5f")
stb_verify_header("${STB_INCLUDE_DIR}/stb_rect_pack.h"         "cc13ffb3eee8b07b82274a16481007ee1f8c852203a89ab41a3ab8a4a9e32db9")
stb_verify_header("${STB_INCLUDE_DIR}/stb_dxt.h"               "8f1a6cc3af7057679a36d32915aff15894521170bcdf95f47c101fdbb1f90500")
stb_verify_header("${STB_INCLUDE_DIR}/stb_perlin.h"            "6dffe969cd7508ce00a29decb404d43e5122d66b14159484c97cc7f0e18238cc")
stb_verify_header("${STB_IMAGE_RESIZE_INCLUDE_DIR}/stb_image_resize2.h" "7420ce124ccd746e47f66d85235eb22838b339ae0689a72d6eb2e3d32dc657d1")

add_library(stb_oracle SHARED stb_oracle.c)

target_include_directories(stb_oracle PRIVATE
    "${STB_INCLUDE_DIR}"
    "${STB_IMAGE_RESIZE_INCLUDE_DIR}"
)

if(MSVC)
    target_compile_definitions(stb_oracle PRIVATE _CRT_SECURE_NO_WARNINGS)
    target_compile_options(stb_oracle PRIVATE /O2)
else()
    # Keep strict IEEE-754 rounding without implicit FMA contraction so the
    # native oracle produces identical float results across Windows (MSVC
    # /fp:precise), Linux (GCC), macOS (Apple Clang Arm64), and .NET.
    target_compile_options(stb_oracle PRIVATE -O3 -ffp-contract=off)
    if(UNIX)
        target_link_libraries(stb_oracle PRIVATE m)
    endif()
endif()

# Uniform output directory across single-config (Make/Ninja) and multi-config (MSVC/Xcode) generators.
set(STB_ORACLE_OUT_DIR "${CMAKE_BINARY_DIR}/out")
set_target_properties(stb_oracle PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${STB_ORACLE_OUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${STB_ORACLE_OUT_DIR}"
    ARCHIVE_OUTPUT_DIRECTORY "${STB_ORACLE_OUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${STB_ORACLE_OUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_RELEASE "${STB_ORACLE_OUT_DIR}"
    ARCHIVE_OUTPUT_DIRECTORY_RELEASE "${STB_ORACLE_OUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_DEBUG "${STB_ORACLE_OUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_DEBUG "${STB_ORACLE_OUT_DIR}"
    ARCHIVE_OUTPUT_DIRECTORY_DEBUG "${STB_ORACLE_OUT_DIR}"
)
