cmake_minimum_required(VERSION 3.20)
project(chess_engine LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Shared library: chess_engine.dll (Windows) / libchess_engine.so (Linux).
add_library(chess_engine SHARED
    src/chess_engine.cpp
    src/eval.cpp
    src/search.cpp
    src/learned_model.cpp
    src/bitboard.cpp
    src/zobrist.cpp
    src/position.cpp
    src/movegen.cpp
    src/uci.cpp
    src/perft.cpp)
target_include_directories(chess_engine PUBLIC include)
target_compile_definitions(chess_engine PRIVATE CHESS_ENGINE_BUILD)

set_target_properties(chess_engine PROPERTIES
    OUTPUT_NAME chess_engine
    POSITION_INDEPENDENT_CODE ON)        # -fPIC on Linux (required for .so)

# Export only the symbols marked with the CHESS_API macro.
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

if (MSVC)
    target_compile_options(chess_engine PRIVATE
        $<$<CONFIG:Release>:/O2 /GL /DNDEBUG /arch:AVX2>
        $<$<CONFIG:Debug>:/Od /Zi>)          # /Zi => .pdb for mixed-mode debugging
    target_link_options(chess_engine PRIVATE
        $<$<CONFIG:Release>:/LTCG>
        $<$<CONFIG:Debug>:/DEBUG>)
else()
    # Portable server baseline. Do NOT use -march=native: the build host may have
    # instructions the server lacks (SIGILL at runtime). Bump only once the server
    # CPU floor is confirmed.
    target_compile_options(chess_engine PRIVATE
        $<$<CONFIG:Release>:-O3 -flto -DNDEBUG -march=x86-64-v2>
        $<$<CONFIG:Debug>:-O0 -g>)
endif()
