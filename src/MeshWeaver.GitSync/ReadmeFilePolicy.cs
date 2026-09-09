using System.Text.Json;

namespace MeshWeaver.GitSync;

/// <summary>Distinguishes a package's declared README node from a generated repository landing page.</summary>
internal readonly record struct ReadmeFilePolicy(bool IsPackage, bool IsDeclaredNode)
{
    internal static ReadmeFilePolicy From(RepoSnapshot snapshot)
    {
        var manifest = snapshot.Files.FirstOrDefault(f =>
            string.Equals(f.Path, "manifest.lock", StringComparison.OrdinalIgnoreCase));
        if (manifest is null)
            return default;

        // A malformed declaration cannot safely decide whether an existing node is retired.
        // Do not turn an unreadable manifest into the display-only case and proceed with pruning.
        using var document = JsonDocument.Parse(manifest.Content);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("module", out var module)
            || module.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(module.GetString())
            || !root.TryGetProperty("files", out var files)
            || files.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("manifest.lock must declare its module and files before Git sync can interpret its README.");

        var path = module.GetString()!.TrimEnd('/') + "/README.md";
        return new ReadmeFilePolicy(true, files.EnumerateObject().Any(f =>
            string.Equals(f.Name, path, StringComparison.OrdinalIgnoreCase)));
    }
}
