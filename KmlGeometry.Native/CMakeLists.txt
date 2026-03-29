cmake_minimum_required(VERSION 3.25)

project(kml_geometry_native LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)

find_package(GEOS CONFIG REQUIRED)
find_package(PROJ CONFIG REQUIRED)
find_package(LibKML CONFIG REQUIRED)
find_package(nlohmann_json CONFIG REQUIRED)

add_library(kml_geometry_native SHARED
    src/kml_geometry_native.cpp
)

target_include_directories(kml_geometry_native PRIVATE src)

target_link_libraries(kml_geometry_native
    PRIVATE
        GEOS::geos_c
        PROJ::proj
        kmldom
        kmlbase
        kmlengine
        nlohmann_json::nlohmann_json
)

target_compile_definitions(kml_geometry_native PRIVATE KML_GEOMETRY_NATIVE_EXPORTS)
