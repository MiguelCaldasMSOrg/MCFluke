cmake_minimum_required(VERSION 3.20)
project(MCFluke VERSION 0.1.0 LANGUAGES C)

include(CTest)
include(GNUInstallDirs)
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)

function(mcfluke_target target)
    target_include_directories(${target} PRIVATE include src)
    if(MSVC)
        target_compile_options(${target} PRIVATE /W4 /WX)
        target_compile_definitions(${target} PRIVATE _CRT_SECURE_NO_WARNINGS)
    else()
        target_compile_options(${target} PRIVATE -Wall -Wextra -Wpedantic -Werror)
    endif()
    set_target_properties(${target} PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/$<0:>"
        LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/$<0:>"
        ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib/$<0:>")
    if(APPLE)
        set_target_properties(${target} PROPERTIES INSTALL_RPATH "@loader_path/../${CMAKE_INSTALL_LIBDIR}")
    elseif(UNIX)
        set_target_properties(${target} PROPERTIES INSTALL_RPATH "$ORIGIN/../${CMAKE_INSTALL_LIBDIR}")
    endif()
endfunction()

add_library(mcfluke SHARED src/mcfluke.c src/transport.c)
mcfluke_target(mcfluke)
target_compile_definitions(mcfluke PRIVATE MCFLUKE_BUILD)
target_include_directories(mcfluke PUBLIC
    $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
    $<INSTALL_INTERFACE:${CMAKE_INSTALL_INCLUDEDIR}>)
set_target_properties(mcfluke PROPERTIES C_VISIBILITY_PRESET hidden)
if(WIN32)
    set_target_properties(mcfluke PROPERTIES PREFIX "")
endif()

add_executable(mcfluke-example examples/c/main.c)
mcfluke_target(mcfluke-example)
target_link_libraries(mcfluke-example PRIVATE mcfluke)

add_executable(mcfluke-host src/host.c)
mcfluke_target(mcfluke-host)
target_link_libraries(mcfluke-host PRIVATE mcfluke)

if(BUILD_TESTING)
    add_executable(mcfluke-tests tests/test_mcfluke.c tests/fake_transport.c src/mcfluke.c)
    mcfluke_target(mcfluke-tests)
    target_compile_definitions(mcfluke-tests PRIVATE MCFLUKE_BUILD)
    add_test(NAME protocol COMMAND mcfluke-tests)
    add_test(NAME native-simulator COMMAND mcfluke-example --port sim://187 --count 2 --interval 0.01)
    find_package(Python3 REQUIRED COMPONENTS Interpreter)
    add_test(NAME bridge COMMAND ${Python3_EXECUTABLE}
        ${CMAKE_CURRENT_SOURCE_DIR}/tests/test_bridge.py $<TARGET_FILE:mcfluke-host>)
    add_test(NAME serial-pty COMMAND ${Python3_EXECUTABLE}
        ${CMAKE_CURRENT_SOURCE_DIR}/tests/test_serial.py $<TARGET_FILE:mcfluke>)
endif()

install(TARGETS mcfluke mcfluke-example mcfluke-host
    RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR}
    LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR}
    ARCHIVE DESTINATION ${CMAKE_INSTALL_LIBDIR})
install(FILES include/mcfluke.h DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})
