using System;
using System.IO;

namespace MeshWeaver.Autocomplete.Test;

/// <summary>
/// Provides paths to test data directories.
/// Uses pre-copied directories from build output to avoid runtime copying.
/// </summary>
public static class TestPaths
{
    public static string SamplesGraph => Path.Combine(AppContext.BaseDirectory, "SamplesGraph");
    public static string SamplesGraphData => Path.Combine(SamplesGraph, "Data");
    public static string SamplesGraphContent => Path.Combine(SamplesGraph, "content");
}
