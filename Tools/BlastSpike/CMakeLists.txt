cmake_minimum_required(VERSION 3.16)
project(blast_cut CXX)
set(CMAKE_CXX_STANDARD 14)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

# Run fetch_blast.sh first: it puts the pinned, patched Blast source here.
set(BLAST ${CMAKE_CURRENT_SOURCE_DIR}/blast CACHE PATH "Path to the blast folder of NVIDIA-Omniverse/PhysX")

file(GLOB BLAST_SOURCES
  ${BLAST}/source/sdk/lowlevel/*.cpp
  ${BLAST}/source/sdk/globals/*.cpp
  ${BLAST}/source/sdk/common/*.cpp
  ${BLAST}/source/sdk/extensions/authoring/*.cpp
  ${BLAST}/source/sdk/extensions/authoringCommon/*.cpp
  ${BLAST}/source/sdk/extensions/authoring/VHACD/src/*.cpp)

add_executable(blast_cut blast_cut.cpp ${BLAST_SOURCES})
target_include_directories(blast_cut PRIVATE
  ${BLAST}/include
  ${BLAST}/include/lowlevel
  ${BLAST}/include/globals
  ${BLAST}/include/extensions/assetutils
  ${BLAST}/include/extensions/authoring
  ${BLAST}/include/extensions/authoringCommon
  ${BLAST}/include/shared/NvFoundation
  ${BLAST}/source/sdk/common
  ${BLAST}/source/sdk/globals
  ${BLAST}/source/sdk/lowlevel
  ${BLAST}/source/sdk/extensions/authoring
  ${BLAST}/source/sdk/extensions/authoringCommon
  ${BLAST}/source/sdk/extensions/authoring/VHACD/inc
  ${BLAST}/source/sdk/extensions/authoring/VHACD/public
  ${BLAST}/source/shared/NsFoundation/include)
target_compile_definitions(blast_cut PRIVATE NDEBUG LOG_COMPONENT="blast_cut")
target_compile_options(blast_cut PRIVATE -w)

if(APPLE)
  # Blast only declares its C API extern "C" on Windows and Linux; without this
  # every declaration disagrees with its definition about linkage.
  target_compile_definitions(blast_cut PRIVATE "NV_C_EXPORT=extern \"C\"")
  # macOS has no <malloc.h>.
  target_include_directories(blast_cut BEFORE PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/macos-shim)
  # Boost Multiprecision (header-only) from Homebrew: brew install boost
  find_path(BOOST_INCLUDE boost/multiprecision/cpp_int.hpp HINTS /opt/homebrew/include /usr/local/include)
  target_include_directories(blast_cut PRIVATE ${BOOST_INCLUDE})
endif()

find_package(Threads REQUIRED)
target_link_libraries(blast_cut PRIVATE Threads::Threads)
