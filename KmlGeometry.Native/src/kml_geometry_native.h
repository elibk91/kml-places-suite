#pragma once

#include <stddef.h>

#if defined(_WIN32)
#  if defined(KML_GEOMETRY_NATIVE_EXPORTS)
#    define KML_GEOMETRY_NATIVE_API __declspec(dllexport)
#  else
#    define KML_GEOMETRY_NATIVE_API __declspec(dllimport)
#  endif
#else
#  define KML_GEOMETRY_NATIVE_API
#endif

extern "C"
{
    KML_GEOMETRY_NATIVE_API int kg_generate_intersection_json(
        const char* request_json_utf8,
        char** result_json_utf8,
        char** error_json_utf8);

    KML_GEOMETRY_NATIVE_API int kg_read_kml_source_json(
        const char* source_path_utf8,
        char** result_json_utf8,
        char** error_json_utf8);

    KML_GEOMETRY_NATIVE_API int kg_read_kml_text_json(
        const char* kml_text_utf8,
        char** result_json_utf8,
        char** error_json_utf8);

    KML_GEOMETRY_NATIVE_API void kg_free_string(char* value);
}
