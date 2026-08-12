using System.Reflection;
using System.Text.Json;
using Xunit;

namespace MeshWeaver.Json.Test;

/// <summary>
/// Loads the committed json-everything capture. 🚨 It is an EMBEDDED resource and every accessor
/// throws when a section is missing or empty — a fixture that silently loaded nothing would turn
/// the whole wire-compatibility suite into a green no-op, which is worse than no suite at all.
/// </summary>
public static class GoldenFixtures
{
    private static readonly JsonDocument Document = Load();

    private static JsonDocument Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .SingleOrDefault(n => n.EndsWith("json-everything-golden.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "The json-everything golden fixture is not embedded in MeshWeaver.Json.Test. "
                + "Without it every wire-compatibility assertion would vanish silently.");
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonDocument.Parse(stream);
    }

    /// <summary>The rows of one fixture section, asserted non-empty.</summary>
    public static IReadOnlyList<JsonElement> Section(string name)
    {
        if (!Document.RootElement.TryGetProperty(name, out var section))
            throw new InvalidOperationException($"Golden fixture has no section '{name}'.");
        var rows = section.EnumerateArray().ToArray();
        if (rows.Length == 0)
            throw new InvalidOperationException($"Golden fixture section '{name}' is empty.");
        return rows;
    }

    /// <summary>A scalar string entry from the fixture root.</summary>
    public static string Text(string name) => Document.RootElement.GetProperty(name).GetString()!;

    /// <summary>xUnit theory data for one fixture section.</summary>
    public static TheoryData<string> Names(string section, string key)
    {
        var data = new TheoryData<string>();
        foreach (var row in Section(section)) data.Add(row.GetProperty(key).GetString()!);
        return data;
    }

    /// <summary>Looks a row up by the value of <paramref name="key"/>.</summary>
    public static JsonElement Row(string section, string key, string value)
        => Section(section).Single(r => r.GetProperty(key).GetString() == value);
}
