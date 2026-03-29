using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using KmlGenerator.Core.Models;

namespace KmlGenerator.Core.Services;

internal static class NativeGeometryLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    static NativeGeometryLibrary()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeGeometryLibrary).Assembly, ResolveLibrary);
    }

    public static NativeIntersectionResult Generate(GenerateKmlRequest request)
    {
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var status = kg_generate_intersection_json(requestJson, out var resultPointer, out var errorPointer);
        try
        {
            if (status != 0)
            {
                var error = errorPointer == IntPtr.Zero
                    ? "Native geometry generation failed."
                    : Marshal.PtrToStringUTF8(errorPointer) ?? "Native geometry generation failed.";
                throw new InvalidOperationException(error);
            }

            var resultJson = resultPointer == IntPtr.Zero
                ? "{}"
                : Marshal.PtrToStringUTF8(resultPointer) ?? "{}";

            return JsonSerializer.Deserialize<NativeIntersectionResult>(resultJson, JsonOptions)
                ?? throw new InvalidOperationException("Native geometry generation returned an empty payload.");
        }
        finally
        {
            if (resultPointer != IntPtr.Zero)
            {
                kg_free_string(resultPointer);
            }

            if (errorPointer != IntPtr.Zero)
            {
                kg_free_string(errorPointer);
            }
        }
    }

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "kml_geometry_native", StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        var repoRoot = FindRepoRoot();
        var vcpkgBin = Path.Combine(repoRoot, ".native", "vcpkg", "installed", "x64-windows", "bin");
        var libraryCandidates = new[]
        {
            Path.Combine(repoRoot, ".native", "build", "kml_geometry_native", "Release", "kml_geometry_native.dll"),
            Path.Combine(AppContext.BaseDirectory, "kml_geometry_native.dll")
        };

        var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!existingPath.Split(';', StringSplitOptions.RemoveEmptyEntries).Contains(vcpkgBin, StringComparer.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable("PATH", $"{vcpkgBin};{existingPath}");
        }

        foreach (var candidate in libraryCandidates)
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        throw new DllNotFoundException("Could not locate kml_geometry_native.dll. Build the native project under .native/build/kml_geometry_native first.");
    }

    private static string FindRepoRoot()
    {
        var candidates = new[]
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        };

        foreach (var candidate in candidates)
        {
            var current = candidate;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (File.Exists(Path.Combine(current, "AGENTS.md"))
                    && Directory.Exists(Path.Combine(current, ".native")))
                {
                    return current;
                }

                current = Path.GetDirectoryName(current);
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root needed to load the native geometry library.");
    }

    [DllImport("kml_geometry_native", CallingConvention = CallingConvention.Cdecl, EntryPoint = "kg_generate_intersection_json")]
    private static extern int kg_generate_intersection_json(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string requestJsonUtf8,
        out IntPtr resultJsonUtf8,
        out IntPtr errorJsonUtf8);

    [DllImport("kml_geometry_native", CallingConvention = CallingConvention.Cdecl, EntryPoint = "kg_free_string")]
    private static extern void kg_free_string(IntPtr value);
}

internal sealed class NativeIntersectionResult
{
    public int IntersectionPolygonCount { get; init; }

    public int FeatureCount { get; init; }

    public int CoveredCellCount { get; init; }

    public BoundingBox Bounds { get; init; } = new();

    public IReadOnlyList<PolygonInput> Polygons { get; init; } = Array.Empty<PolygonInput>();
}
