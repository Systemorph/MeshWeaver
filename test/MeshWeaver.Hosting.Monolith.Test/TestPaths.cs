using System;
using System.IO;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Provides paths to test data directories.
/// Uses pre-copied directories from build output to avoid runtime copying.
/// </summary>
public static class TestPaths
{
    /// <summary>
    /// Path to the samples/Graph directory (pre-copied at build time).
    /// </summary>
    public static string SamplesGraph => Path.Combine(AppContext.BaseDirectory, "SamplesGraph");

    /// <summary>
    /// Path to the samples/Graph/Data directory.
    /// </summary>
    public static string SamplesGraphData => Path.Combine(SamplesGraph, "Data");

    /// <summary>
    /// Path to the samples/Graph/content directory.
    /// </summary>
    public static string SamplesGraphContent => Path.Combine(SamplesGraph, "content");
}
