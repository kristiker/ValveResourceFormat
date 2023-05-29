cmake_minimum_required(VERSION 3.0)
project(SpirvReflector)

# Set the default build type to Release if not specified
if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

set(SOURCES
  dllmain.cpp
  SpirvReflector.cpp
)

set(HEADERS
  framework.h
  SpirvReflector.h
)

add_library(SpirvReflector SHARED ${SOURCES} ${HEADERS})

set_property(TARGET SpirvReflector PROPERTY CXX_STANDARD 17)

# Add project references
add_subdirectory(spirv-cross)
target_link_libraries(SpirvReflector PRIVATE
  spirv-cross-c
  spirv-cross-core
  spirv-cross-hlsl
  spirv-cross-glsl
  spirv-cross-reflect
)

# Set the output directory
set_target_properties(SpirvReflector PROPERTIES
  ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_CURRENT_BINARY_DIR}
  LIBRARY_OUTPUT_DIRECTORY ${CMAKE_CURRENT_BINARY_DIR}
  RUNTIME_OUTPUT_DIRECTORY ${CMAKE_CURRENT_BINARY_DIR}
)
