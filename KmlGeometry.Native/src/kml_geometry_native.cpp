#include "kml_geometry_native.h"

#include <algorithm>
#include <cstdlib>
#include <cstring>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include <geos_c.h>
#include <nlohmann/json.hpp>
#include <proj.h>

namespace
{
    using json = nlohmann::json;

    constexpr double kMetersPerMile = 1609.344;
    constexpr double kDegreesToRadians = 3.14159265358979323846 / 180.0;
    constexpr double kRadiansToDegrees = 180.0 / 3.14159265358979323846;

    struct LatLon
    {
        double latitude{};
        double longitude{};
    };

    struct XY
    {
        double x{};
        double y{};
    };

    struct LineStringInput
    {
        std::vector<LatLon> coordinates;
    };

    struct PolygonInput
    {
        std::vector<LatLon> outer_ring;
        std::vector<std::vector<LatLon>> inner_rings;
    };

    struct FeatureInput
    {
        std::string category;
        std::string label;
        std::string geometry_type;
        std::vector<LatLon> points;
        std::vector<LineStringInput> lines;
        std::vector<PolygonInput> polygons;
    };

    struct OutputPolygon
    {
        std::vector<LatLon> outer_ring;
        std::vector<std::vector<LatLon>> inner_rings;
    };

    struct NativeResult
    {
        int intersection_polygon_count{};
        int feature_count{};
        int covered_cell_count{};
        double min_latitude{};
        double max_latitude{};
        double min_longitude{};
        double max_longitude{};
        std::vector<OutputPolygon> polygons;
    };

    struct ProjContextDeleter
    {
        void operator()(PJ_CONTEXT* context) const noexcept
        {
            if (context != nullptr)
            {
                proj_context_destroy(context);
            }
        }
    };

    struct ProjHandleDeleter
    {
        void operator()(PJ* projection) const noexcept
        {
            if (projection != nullptr)
            {
                proj_destroy(projection);
            }
        }
    };

    struct GeosContextDeleter
    {
        void operator()(std::remove_pointer_t<GEOSContextHandle_t>* context) const noexcept
        {
            if (context != nullptr)
            {
                GEOS_finish_r(context);
            }
        }
    };

    struct GeosGeometryDeleter
    {
        explicit GeosGeometryDeleter(GEOSContextHandle_t geos_context) : context(geos_context)
        {
        }

        void operator()(GEOSGeometry* geometry) const noexcept
        {
            if (geometry != nullptr)
            {
                GEOSGeom_destroy_r(context, geometry);
            }
        }

        GEOSContextHandle_t context;
    };

    using ScopedProjContext = std::unique_ptr<PJ_CONTEXT, ProjContextDeleter>;
    using ScopedProjHandle = std::unique_ptr<PJ, ProjHandleDeleter>;
    using ScopedGeosContext = std::unique_ptr<std::remove_pointer_t<GEOSContextHandle_t>, GeosContextDeleter>;
    using ScopedGeometry = std::unique_ptr<GEOSGeometry, GeosGeometryDeleter>;

    [[noreturn]] void fail(std::string_view message)
    {
        throw std::runtime_error(std::string(message));
    }

    std::string to_lower(std::string value)
    {
        std::ranges::transform(value, value.begin(), [](unsigned char character) { return static_cast<char>(std::tolower(character)); });
        return value;
    }

    LatLon parse_coordinate(const json& value)
    {
        return {
            .latitude = value.at("latitude").get<double>(),
            .longitude = value.at("longitude").get<double>()
        };
    }

    FeatureInput parse_feature(const json& value)
    {
        FeatureInput feature
        {
            .category = value.at("category").get<std::string>(),
            .label = value.at("label").get<std::string>(),
            .geometry_type = to_lower(value.at("geometryType").get<std::string>())
        };

        if (value.contains("points"))
        {
            for (const auto& point : value.at("points"))
            {
                feature.points.push_back(parse_coordinate(point));
            }
        }

        if (value.contains("lines"))
        {
            for (const auto& line : value.at("lines"))
            {
                LineStringInput parsed_line;
                for (const auto& coordinate : line.at("coordinates"))
                {
                    parsed_line.coordinates.push_back(parse_coordinate(coordinate));
                }

                feature.lines.push_back(std::move(parsed_line));
            }
        }

        if (value.contains("polygons"))
        {
            for (const auto& polygon : value.at("polygons"))
            {
                PolygonInput parsed_polygon;
                for (const auto& coordinate : polygon.at("outerRing"))
                {
                    parsed_polygon.outer_ring.push_back(parse_coordinate(coordinate));
                }

                if (polygon.contains("innerRings"))
                {
                    for (const auto& ring : polygon.at("innerRings"))
                    {
                        std::vector<LatLon> parsed_ring;
                        for (const auto& coordinate : ring)
                        {
                            parsed_ring.push_back(parse_coordinate(coordinate));
                        }

                        parsed_polygon.inner_rings.push_back(std::move(parsed_ring));
                    }
                }

                feature.polygons.push_back(std::move(parsed_polygon));
            }
        }

        return feature;
    }

    std::vector<FeatureInput> materialize_features(const json& request)
    {
        std::vector<FeatureInput> features;
        if (request.contains("features") && request.at("features").is_array() && !request.at("features").empty())
        {
            for (const auto& feature : request.at("features"))
            {
                features.push_back(parse_feature(feature));
            }

            return features;
        }

        if (!request.contains("locations"))
        {
            return features;
        }

        for (const auto& location : request.at("locations"))
        {
            FeatureInput feature
            {
                .category = location.at("category").get<std::string>(),
                .label = location.at("label").get<std::string>(),
                .geometry_type = "point"
            };

            feature.points.push_back({
                .latitude = location.at("latitude").get<double>(),
                .longitude = location.at("longitude").get<double>()
            });

            features.push_back(std::move(feature));
        }

        return features;
    }

    std::vector<LatLon> enumerate_coordinates(const std::vector<FeatureInput>& features)
    {
        std::vector<LatLon> coordinates;
        for (const auto& feature : features)
        {
            coordinates.insert(coordinates.end(), feature.points.begin(), feature.points.end());
            for (const auto& line : feature.lines)
            {
                coordinates.insert(coordinates.end(), line.coordinates.begin(), line.coordinates.end());
            }

            for (const auto& polygon : feature.polygons)
            {
                coordinates.insert(coordinates.end(), polygon.outer_ring.begin(), polygon.outer_ring.end());
                for (const auto& ring : polygon.inner_rings)
                {
                    coordinates.insert(coordinates.end(), ring.begin(), ring.end());
                }
            }
        }

        return coordinates;
    }

    class Projection
    {
    public:
        explicit Projection(const std::vector<LatLon>& coordinates)
        {
            if (coordinates.empty())
            {
                fail("At least one coordinate is required.");
            }

            auto [min_latitude, max_latitude] = std::minmax_element(
                coordinates.begin(),
                coordinates.end(),
                [](const auto& left, const auto& right) { return left.latitude < right.latitude; });
            auto [min_longitude, max_longitude] = std::minmax_element(
                coordinates.begin(),
                coordinates.end(),
                [](const auto& left, const auto& right) { return left.longitude < right.longitude; });

            const auto center_latitude = (min_latitude->latitude + max_latitude->latitude) / 2.0;
            const auto center_longitude = (min_longitude->longitude + max_longitude->longitude) / 2.0;

            context_ = ScopedProjContext(proj_context_create());
            if (!context_)
            {
                fail("Could not create PROJ context.");
            }

            std::ostringstream local_definition;
            local_definition
                << "+proj=aeqd +lat_0=" << center_latitude
                << " +lon_0=" << center_longitude
                << " +datum=WGS84 +units=m +no_defs";

            auto* local_projection = proj_create(context_.get(), local_definition.str().c_str());
            if (local_projection == nullptr)
            {
                fail("Could not create local PROJ transform.");
            }

            projection_ = ScopedProjHandle(local_projection);
            if (!projection_)
            {
                fail("Could not create local PROJ transform.");
            }
        }

        XY project(const LatLon& coordinate) const
        {
            const auto input = proj_coord(coordinate.longitude * kDegreesToRadians, coordinate.latitude * kDegreesToRadians, 0.0, 0.0);
            const auto output = proj_trans(projection_.get(), PJ_FWD, input);
            return { .x = output.xy.x, .y = output.xy.y };
        }

        LatLon inverse_project(const XY& coordinate) const
        {
            const auto input = proj_coord(coordinate.x, coordinate.y, 0.0, 0.0);
            const auto output = proj_trans(projection_.get(), PJ_INV, input);
            return { .latitude = output.lp.phi * kRadiansToDegrees, .longitude = output.lp.lam * kRadiansToDegrees };
        }

    private:
        ScopedProjContext context_;
        ScopedProjHandle projection_;
    };

    class GeosSession
    {
    public:
        GeosSession()
            : context_(GEOS_init_r())
        {
            if (context_.get() == nullptr)
            {
                fail("Could not initialize GEOS.");
            }

            GEOSContext_setErrorMessageHandler_r(
                context_.get(),
                [](const char* message, void* user_data)
                {
                    if (message != nullptr && user_data != nullptr)
                    {
                        static_cast<std::string*>(user_data)->append(message);
                    }
                },
                &last_error_);
        }

        GEOSContextHandle_t handle() const
        {
            return context_.get();
        }

        ScopedGeometry wrap(GEOSGeometry* geometry) const
        {
            return ScopedGeometry(geometry, GeosGeometryDeleter(context_.get()));
        }

        [[noreturn]] void fail_with_last_error(std::string_view message) const
        {
            if (!last_error_.empty())
            {
                throw std::runtime_error(std::string(message) + ": " + last_error_);
            }

            throw std::runtime_error(std::string(message));
        }

    private:
        ScopedGeosContext context_;
        std::string last_error_;
    };

    GEOSCoordSequence* build_coord_sequence(
        const GeosSession& session,
        const Projection& projection,
        const std::vector<LatLon>& coordinates,
        bool close_ring)
    {
        if (coordinates.empty())
        {
            fail("Encountered an empty coordinate sequence.");
        }

        const auto needs_close = close_ring &&
            (coordinates.front().latitude != coordinates.back().latitude
                || coordinates.front().longitude != coordinates.back().longitude);
        const auto point_count = coordinates.size() + (needs_close ? 1u : 0u);
        auto* sequence = GEOSCoordSeq_create_r(session.handle(), static_cast<unsigned int>(point_count), 2);
        if (sequence == nullptr)
        {
            session.fail_with_last_error("Could not allocate GEOS coordinate sequence");
        }

        for (std::size_t index = 0; index < coordinates.size(); ++index)
        {
            const auto projected = projection.project(coordinates[index]);
            GEOSCoordSeq_setX_r(session.handle(), sequence, static_cast<unsigned int>(index), projected.x);
            GEOSCoordSeq_setY_r(session.handle(), sequence, static_cast<unsigned int>(index), projected.y);
        }

        if (needs_close)
        {
            const auto projected = projection.project(coordinates.front());
            GEOSCoordSeq_setX_r(session.handle(), sequence, static_cast<unsigned int>(point_count - 1), projected.x);
            GEOSCoordSeq_setY_r(session.handle(), sequence, static_cast<unsigned int>(point_count - 1), projected.y);
        }

        return sequence;
    }

    ScopedGeometry build_feature_geometry(const GeosSession& session, const Projection& projection, const FeatureInput& feature)
    {
        if (feature.geometry_type == "point")
        {
            std::vector<GEOSGeometry*> points;
            points.reserve(feature.points.size());
            for (const auto& point : feature.points)
            {
                std::vector<LatLon> single_point { point };
                auto* sequence = build_coord_sequence(session, projection, single_point, false);
                auto* geometry = GEOSGeom_createPoint_r(session.handle(), sequence);
                if (geometry == nullptr)
                {
                    GEOSCoordSeq_destroy_r(session.handle(), sequence);
                    session.fail_with_last_error("Could not create GEOS point");
                }

                points.push_back(geometry);
            }

            if (points.size() == 1)
            {
                return session.wrap(points.front());
            }

            auto collection = session.wrap(GEOSGeom_createCollection_r(
                session.handle(),
                GEOS_MULTIPOINT,
                points.data(),
                static_cast<unsigned int>(points.size())));
            if (!collection)
            {
                for (auto* geometry : points)
                {
                    GEOSGeom_destroy_r(session.handle(), geometry);
                }

                session.fail_with_last_error("Could not create GEOS multipoint");
            }

            return collection;
        }

        if (feature.geometry_type == "linestring")
        {
            std::vector<GEOSGeometry*> lines;
            lines.reserve(feature.lines.size());
            for (const auto& line : feature.lines)
            {
                auto* sequence = build_coord_sequence(session, projection, line.coordinates, false);
                auto* geometry = GEOSGeom_createLineString_r(session.handle(), sequence);
                if (geometry == nullptr)
                {
                    GEOSCoordSeq_destroy_r(session.handle(), sequence);
                    session.fail_with_last_error("Could not create GEOS line string");
                }

                lines.push_back(geometry);
            }

            if (lines.size() == 1)
            {
                return session.wrap(lines.front());
            }

            auto collection = session.wrap(GEOSGeom_createCollection_r(
                session.handle(),
                GEOS_MULTILINESTRING,
                lines.data(),
                static_cast<unsigned int>(lines.size())));
            if (!collection)
            {
                for (auto* geometry : lines)
                {
                    GEOSGeom_destroy_r(session.handle(), geometry);
                }

                session.fail_with_last_error("Could not create GEOS multi-line string");
            }

            return collection;
        }

        if (feature.geometry_type == "polygon")
        {
            std::vector<GEOSGeometry*> polygons;
            polygons.reserve(feature.polygons.size());
            for (const auto& polygon : feature.polygons)
            {
                auto* outer_sequence = build_coord_sequence(session, projection, polygon.outer_ring, true);
                auto* outer_ring = GEOSGeom_createLinearRing_r(session.handle(), outer_sequence);
                if (outer_ring == nullptr)
                {
                    GEOSCoordSeq_destroy_r(session.handle(), outer_sequence);
                    session.fail_with_last_error("Could not create GEOS polygon outer ring");
                }

                std::vector<GEOSGeometry*> holes;
                holes.reserve(polygon.inner_rings.size());
                for (const auto& hole : polygon.inner_rings)
                {
                    auto* hole_sequence = build_coord_sequence(session, projection, hole, true);
                    auto* hole_ring = GEOSGeom_createLinearRing_r(session.handle(), hole_sequence);
                    if (hole_ring == nullptr)
                    {
                        GEOSCoordSeq_destroy_r(session.handle(), hole_sequence);
                        for (auto* existing_hole : holes)
                        {
                            GEOSGeom_destroy_r(session.handle(), existing_hole);
                        }

                        GEOSGeom_destroy_r(session.handle(), outer_ring);
                        session.fail_with_last_error("Could not create GEOS polygon hole ring");
                    }

                    holes.push_back(hole_ring);
                }

                auto* geometry = GEOSGeom_createPolygon_r(
                    session.handle(),
                    outer_ring,
                    holes.empty() ? nullptr : holes.data(),
                    static_cast<unsigned int>(holes.size()));
                if (geometry == nullptr)
                {
                    for (auto* hole : holes)
                    {
                        GEOSGeom_destroy_r(session.handle(), hole);
                    }

                    GEOSGeom_destroy_r(session.handle(), outer_ring);
                    session.fail_with_last_error("Could not create GEOS polygon");
                }

                polygons.push_back(geometry);
            }

            if (polygons.size() == 1)
            {
                return session.wrap(polygons.front());
            }

            auto collection = session.wrap(GEOSGeom_createCollection_r(
                session.handle(),
                GEOS_MULTIPOLYGON,
                polygons.data(),
                static_cast<unsigned int>(polygons.size())));
            if (!collection)
            {
                for (auto* geometry : polygons)
                {
                    GEOSGeom_destroy_r(session.handle(), geometry);
                }

                session.fail_with_last_error("Could not create GEOS multipolygon");
            }

            return collection;
        }

        fail("Unsupported geometry type: " + feature.geometry_type);
    }

    ScopedGeometry buffer_feature(const GeosSession& session, ScopedGeometry geometry, double radius_meters)
    {
        auto buffered = session.wrap(GEOSBuffer_r(session.handle(), geometry.get(), radius_meters, 16));
        if (!buffered)
        {
            session.fail_with_last_error("Could not buffer geometry");
        }

        return buffered;
    }

    ScopedGeometry unary_union(const GeosSession& session, std::vector<ScopedGeometry>& geometries)
    {
        std::vector<GEOSGeometry*> raw_geometries;
        raw_geometries.reserve(geometries.size());
        for (auto& geometry : geometries)
        {
            raw_geometries.push_back(geometry.release());
        }

        auto collection = session.wrap(GEOSGeom_createCollection_r(
            session.handle(),
            GEOS_GEOMETRYCOLLECTION,
            raw_geometries.data(),
            static_cast<unsigned int>(raw_geometries.size())));
        if (!collection)
        {
            for (auto* geometry : raw_geometries)
            {
                GEOSGeom_destroy_r(session.handle(), geometry);
            }

            session.fail_with_last_error("Could not build GEOS geometry collection for union");
        }

        auto unioned = session.wrap(GEOSUnaryUnion_r(session.handle(), collection.get()));
        if (!unioned)
        {
            session.fail_with_last_error("Could not unary-union category geometries");
        }

        return unioned;
    }

    std::map<std::string, double, std::less<>> parse_category_radii(const json& request)
    {
        std::map<std::string, double, std::less<>> radii;
        if (request.contains("categoryRadiusMiles"))
        {
            for (const auto& [key, value] : request.at("categoryRadiusMiles").items())
            {
                radii.emplace(to_lower(key), value.get<double>());
            }
        }

        return radii;
    }

    double resolve_radius_miles(
        const std::map<std::string, double, std::less<>>& category_radii,
        const std::string& category,
        double default_radius_miles)
    {
        const auto match = category_radii.find(to_lower(category));
        return match == category_radii.end() ? default_radius_miles : match->second;
    }

    std::vector<LatLon> read_ring(const GeosSession& session, const Projection& projection, const GEOSGeometry* ring_geometry)
    {
        const GEOSCoordSequence* sequence = GEOSGeom_getCoordSeq_r(session.handle(), ring_geometry);
        if (sequence == nullptr)
        {
            session.fail_with_last_error("Could not read GEOS coordinate sequence");
        }

        unsigned int size = 0;
        GEOSCoordSeq_getSize_r(session.handle(), sequence, &size);
        std::vector<LatLon> ring;
        ring.reserve(size);
        for (unsigned int index = 0; index < size; ++index)
        {
            double x = 0.0;
            double y = 0.0;
            GEOSCoordSeq_getX_r(session.handle(), sequence, index, &x);
            GEOSCoordSeq_getY_r(session.handle(), sequence, index, &y);
            ring.push_back(projection.inverse_project({ .x = x, .y = y }));
        }

        return ring;
    }

    void extract_polygons(const GeosSession& session, const Projection& projection, const GEOSGeometry* geometry, std::vector<OutputPolygon>& polygons)
    {
        if (geometry == nullptr)
        {
            return;
        }

        const int geometry_type = GEOSGeomTypeId_r(session.handle(), geometry);
        if (geometry_type == GEOS_POLYGON)
        {
            OutputPolygon polygon;
            polygon.outer_ring = read_ring(session, projection, GEOSGetExteriorRing_r(session.handle(), geometry));
            const int hole_count = GEOSGetNumInteriorRings_r(session.handle(), geometry);
            for (int hole_index = 0; hole_index < hole_count; ++hole_index)
            {
                polygon.inner_rings.push_back(
                    read_ring(session, projection, GEOSGetInteriorRingN_r(session.handle(), geometry, hole_index)));
            }

            polygons.push_back(std::move(polygon));
            return;
        }

        if (geometry_type == GEOS_MULTIPOLYGON || geometry_type == GEOS_GEOMETRYCOLLECTION)
        {
            const int part_count = GEOSGetNumGeometries_r(session.handle(), geometry);
            for (int part_index = 0; part_index < part_count; ++part_index)
            {
                extract_polygons(session, projection, GEOSGetGeometryN_r(session.handle(), geometry, part_index), polygons);
            }
        }
    }

    NativeResult generate_native_result(const std::vector<FeatureInput>& features, const json& request)
    {
        const auto coordinates = enumerate_coordinates(features);
        Projection projection(coordinates);
        GeosSession geos;
        const auto category_radii = parse_category_radii(request);
        const auto default_radius_miles = request.value("radiusMiles", request.value("defaultRadiusMiles", 0.5));

        std::map<std::string, std::vector<ScopedGeometry>, std::less<>> category_buffers;
        for (const auto& feature : features)
        {
            auto geometry = build_feature_geometry(geos, projection, feature);
            const auto radius_miles = resolve_radius_miles(category_radii, feature.category, default_radius_miles);
            category_buffers[to_lower(feature.category)].push_back(
                buffer_feature(geos, std::move(geometry), radius_miles * kMetersPerMile));
        }

        std::vector<ScopedGeometry> category_unions;
        category_unions.reserve(category_buffers.size());
        for (auto& [_, buffered_geometries] : category_buffers)
        {
            category_unions.push_back(unary_union(geos, buffered_geometries));
        }

        auto intersection = std::move(category_unions.front());
        for (std::size_t index = 1; index < category_unions.size(); ++index)
        {
            auto next_intersection = geos.wrap(GEOSIntersection_r(geos.handle(), intersection.get(), category_unions[index].get()));
            if (!next_intersection)
            {
                geos.fail_with_last_error("Could not intersect category buffers");
            }

            intersection = std::move(next_intersection);
        }

        if (GEOSisEmpty_r(geos.handle(), intersection.get()))
        {
            return {
                .intersection_polygon_count = 0,
                .feature_count = static_cast<int>(features.size()),
                .covered_cell_count = 0
            };
        }

        auto valid_intersection = geos.wrap(GEOSMakeValid_r(geos.handle(), intersection.get()));
        if (valid_intersection)
        {
            intersection = std::move(valid_intersection);
        }

        NativeResult result
        {
            .feature_count = static_cast<int>(features.size())
        };

        extract_polygons(geos, projection, intersection.get(), result.polygons);
        result.intersection_polygon_count = static_cast<int>(result.polygons.size());
        result.covered_cell_count = result.intersection_polygon_count;

        if (!result.polygons.empty())
        {
            std::vector<LatLon> all_coordinates;
            for (const auto& polygon : result.polygons)
            {
                all_coordinates.insert(all_coordinates.end(), polygon.outer_ring.begin(), polygon.outer_ring.end());
                for (const auto& ring : polygon.inner_rings)
                {
                    all_coordinates.insert(all_coordinates.end(), ring.begin(), ring.end());
                }
            }

            const auto [min_latitude, max_latitude] = std::minmax_element(
                all_coordinates.begin(),
                all_coordinates.end(),
                [](const auto& left, const auto& right) { return left.latitude < right.latitude; });
            const auto [min_longitude, max_longitude] = std::minmax_element(
                all_coordinates.begin(),
                all_coordinates.end(),
                [](const auto& left, const auto& right) { return left.longitude < right.longitude; });

            result.min_latitude = min_latitude->latitude;
            result.max_latitude = max_latitude->latitude;
            result.min_longitude = min_longitude->longitude;
            result.max_longitude = max_longitude->longitude;
        }

        return result;
    }

    json serialize_result(const NativeResult& result)
    {
        json payload =
        {
            { "intersectionPolygonCount", result.intersection_polygon_count },
            { "featureCount", result.feature_count },
            { "coveredCellCount", result.covered_cell_count },
            { "bounds", {
                { "minLatitude", result.min_latitude },
                { "maxLatitude", result.max_latitude },
                { "minLongitude", result.min_longitude },
                { "maxLongitude", result.max_longitude }
            } },
            { "polygons", json::array() }
        };

        for (const auto& polygon : result.polygons)
        {
            json serialized_polygon =
            {
                { "outerRing", json::array() },
                { "innerRings", json::array() }
            };

            for (const auto& coordinate : polygon.outer_ring)
            {
                serialized_polygon["outerRing"].push_back({
                    { "latitude", coordinate.latitude },
                    { "longitude", coordinate.longitude }
                });
            }

            for (const auto& ring : polygon.inner_rings)
            {
                json serialized_ring = json::array();
                for (const auto& coordinate : ring)
                {
                    serialized_ring.push_back({
                        { "latitude", coordinate.latitude },
                        { "longitude", coordinate.longitude }
                    });
                }

                serialized_polygon["innerRings"].push_back(std::move(serialized_ring));
            }

            payload["polygons"].push_back(std::move(serialized_polygon));
        }

        return payload;
    }

    char* copy_utf8(const std::string& value)
    {
        auto* buffer = static_cast<char*>(std::malloc(value.size() + 1));
        if (buffer == nullptr)
        {
            throw std::bad_alloc();
        }

        std::memcpy(buffer, value.c_str(), value.size() + 1);
        return buffer;
    }
}

extern "C"
{
    int kg_generate_intersection_json(const char* request_json_utf8, char** result_json_utf8, char** error_json_utf8)
    {
        if (result_json_utf8 == nullptr || error_json_utf8 == nullptr)
        {
            return -1;
        }

        *result_json_utf8 = nullptr;
        *error_json_utf8 = nullptr;

        try
        {
            if (request_json_utf8 == nullptr)
            {
                fail("Request JSON is required.");
            }

            const auto request = json::parse(request_json_utf8);
            const auto features = materialize_features(request);
            if (features.empty())
            {
                fail("At least one feature is required.");
            }

            const auto result = generate_native_result(features, request);
            *result_json_utf8 = copy_utf8(serialize_result(result).dump());
            return 0;
        }
        catch (const std::exception& exception)
        {
            *error_json_utf8 = copy_utf8(exception.what());
            return -1;
        }
    }

    void kg_free_string(char* value)
    {
        if (value != nullptr)
        {
            std::free(value);
        }
    }
}
