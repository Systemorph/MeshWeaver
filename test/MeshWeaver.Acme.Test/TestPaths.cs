using System;
using System.IO;

namespace MeshWeaver.Acme.Test;

public static class TestPaths
{
    public static string SamplesGraph => Path.Combine(AppContext.BaseDirectory, "SamplesGraph");
    public static string SamplesGraphData => Path.Combine(SamplesGraph, "Data");
}
