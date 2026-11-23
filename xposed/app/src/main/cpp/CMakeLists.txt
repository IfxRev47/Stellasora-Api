cmake_minimum_required(VERSION 3.22.1)

project("cn")


include_directories(${PROJECT_SOURCE_DIR}/Dobby)
include_directories(${PROJECT_SOURCE_DIR}/)
include_directories(${PROJECT_SOURCE_DIR}/xdl/include)
link_directories(${PROJECT_SOURCE_DIR}/Dobby/arm64-v8a)

add_library(${CMAKE_PROJECT_NAME} SHARED
        native-lib.cpp
        xdl/xdl.c
        xdl/xdl_util.c
        xdl/xdl_lzma.c
        xdl/xdl_iterate.c
        xdl/xdl_linker.c
)


target_link_libraries(${CMAKE_PROJECT_NAME}
        android
        log
        dobby)