cmake_minimum_required(VERSION 3.10)
project(engine)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(Threads REQUIRED)
find_package(PkgConfig REQUIRED)
pkg_check_modules(HTTPLIB REQUIRED IMPORTED_TARGET cpp-httplib)

add_executable(engine src/main.cpp)
target_link_libraries(engine PRIVATE Threads::Threads PkgConfig::HTTPLIB)
