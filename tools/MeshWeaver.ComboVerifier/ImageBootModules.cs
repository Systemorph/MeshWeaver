using System.Collections.Immutable;
using System.Text.Json;

namespace MeshWeaver.ComboVerifier;

/// <summary>
/// The candidate image's BOOT modules: what the portal itself activates from its own
/// <c>Modules:Assemblies</c> before any registry module lands — and what the combo gate must
/// therefore activate too (MeshWeaver#4413).
///
/// <para>🚨 <b>The defect this closes.</b> The gate host is the candidate's <c>/app</c> with the
/// tester laid beside it, but the tester composes its OWN <c>Modules:Assemblies</c> in memory —
/// the image-shipped set (empty) plus the combo's landed modules — so nothing the IMAGE asks for
/// was ever activated. A control image asks for <c>MeshWeaver.Fleet.Control.dll</c> (the Hosting
/// NodeTypes, compiled at image-build time) through <c>Modules__Assemblies__100</c> in its
/// container environment, and a control instance's <c>Deployments</c> records are typed
/// <c>Hosting/Deployment</c>. Without that module the gate refused the install —
/// <c>NodeType(s) not registered: Hosting/Deployment</c> — on every candidate offered to a
/// customer estate's control instance, a Red about the CONTENT whose cause was the INSTRUMENT.
/// The instance itself reads its Deployments fine on the very same image line.</para>
///
/// <para><b>How the portal binds the list, and so how this reads it.</b> Configuration layers
/// <c>appsettings.json</c>, then <c>appsettings.{Environment}.json</c>, then environment
/// variables, and an ARRAY merges BY INDEX: <c>Modules__Assemblies__100</c> ADDS an entry far past
/// the baseline instead of replacing one, while <c>Modules__Assemblies__0</c> would replace the
/// first. Entries are then activated in index order. This reproduces exactly that, over the
/// image's own files and its own <c>Config.Env</c> — never a list written down here, which would
/// be the pin that goes stale the day an image adds a boot module.</para>
///
/// <para><b>Landed wins.</b> A boot module the combo ALSO lands is taken from the landed copy only,
/// the same precedence <c>MeshBuilder.ResolveModulePath</c> gives a running portal (landed →
/// image → app closure).</para>
/// </summary>
public static class ImageBootModules
{
    /// <summary>The configuration section the portal activates modules from.</summary>
    private const string Section = "Modules:Assemblies";

    private static readonly JsonDocumentOptions Lenient = new()
    {
        // The portal's appsettings.json carries // comments and trailing commas; the host's
        // JSON configuration provider accepts both, so this must too.
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The entries the portal's configuration resolves under <c>Modules:Assemblies</c>, in index
    /// order: <paramref name="appSettings"/>, then <paramref name="environmentSettings"/>, then the
    /// image's environment (<c>Modules__Assemblies__N</c>, case-insensitive, <c>__</c> as the
    /// separator), each layer replacing an index the earlier ones set. Blank entries are dropped.
    /// </summary>
    /// <param name="appSettings">The image's <c>appsettings.json</c> text, or null when it has none.</param>
    /// <param name="environmentSettings">Its <c>appsettings.{Environment}.json</c>, or null.</param>
    /// <param name="imageEnvironment">The image's <c>Config.Env</c> as <c>NAME=value</c> lines.</param>
    public static ImmutableArray<string> Declared(
        string? appSettings, string? environmentSettings, IEnumerable<string> imageEnvironment)
    {
        var byIndex = new SortedDictionary<int, string>();
        Layer(byIndex, appSettings);
        Layer(byIndex, environmentSettings);
        foreach (var line in imageEnvironment)
        {
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Replace("__", ":", StringComparison.Ordinal);
            if (!key.StartsWith(Section + ":", StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(key[(Section.Length + 1)..], out var index) && index >= 0)
                byIndex[index] = line[(eq + 1)..];
        }
        return [.. byIndex.Values.Select(v => v.Trim()).Where(v => v.Length > 0)];
    }

    /// <summary>The environment the portal's host selects its <c>appsettings.{Environment}.json</c>
    /// by — <c>ASPNETCORE_ENVIRONMENT</c>, else <c>DOTNET_ENVIRONMENT</c>, else <c>Production</c>,
    /// the host builder's own default.
    ///
    /// <para>Names compare case-insensitively, as configuration's do. A name the image sets twice
    /// modulo case (<c>ASPNETCORE_ENVIRONMENT</c> and <c>aspnetcore_environment</c> are two distinct
    /// variables on Linux, and <c>Config.Env</c> may carry both) resolves LAST-wins — the same rule
    /// <see cref="Declared"/> applies to <c>Modules__Assemblies__N</c> — and never throws: an
    /// unreadable environment is the gate's NotVerifiable, never a crash.</para></summary>
    /// <param name="imageEnvironment">The image's <c>Config.Env</c> lines.</param>
    public static string HostEnvironment(IEnumerable<string> imageEnvironment)
    {
        var env = imageEnvironment
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .Aggregate(ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase),
                (acc, p) => acc.SetItem(p[0], p[1]));
        return new[] { "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT" }
            .Select(k => env.GetValueOrDefault(k))
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "Production";
    }

    /// <summary>
    /// Resolves <paramref name="declared"/> against the composed gate host, the way
    /// <c>MeshBuilder.ResolveModulePath</c> resolves it in the portal — <c>modules/{name}/{entry}</c>,
    /// else <c>{entry}</c> at the app root — and maps each found file to its path inside the
    /// container, where the host is mounted at <paramref name="mount"/>.
    /// </summary>
    /// <param name="hostDir">The composed gate host on this machine (a copy of the portal's /app).</param>
    /// <param name="declared">The entries <see cref="Declared"/> read.</param>
    /// <param name="landed">The combo's landed modules, by entry-assembly name: these win.</param>
    /// <param name="mount">Where <paramref name="hostDir"/> is mounted in the container.</param>
    public static ImageBootModuleSet Resolve(
        string hostDir, IReadOnlyList<string> declared, IEnumerable<string> landed, string mount)
    {
        var landedNames = landed.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var loaded = ImmutableArray.CreateBuilder<string>();
        var shadowed = ImmutableArray.CreateBuilder<string>();
        var absent = ImmutableArray.CreateBuilder<string>();
        foreach (var entry in declared.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // An absolute entry names a path in the PORTAL's filesystem, which the gate host does
            // not reproduce — reported, never guessed at.
            if (Path.IsPathRooted(entry))
            {
                absent.Add(entry);
                continue;
            }
            var name = Path.GetFileNameWithoutExtension(entry);
            if (landedNames.Contains(name))
            {
                shadowed.Add(entry);
                continue;
            }
            if (File.Exists(Path.Combine(hostDir, "modules", name, entry)))
                loaded.Add($"{mount}/modules/{name}/{entry}");
            else if (File.Exists(Path.Combine(hostDir, entry)))
                loaded.Add($"{mount}/{entry}");
            else
                absent.Add(entry);
        }
        return new ImageBootModuleSet
        {
            Paths = loaded.ToImmutable(),
            ShadowedByLanded = shadowed.ToImmutable(),
            Absent = absent.ToImmutable(),
        };
    }

    private static void Layer(SortedDictionary<int, string> byIndex, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;
        using var doc = JsonDocument.Parse(json, Lenient);
        if (!TryProperty(doc.RootElement, "Modules", out var modules)
            || !TryProperty(modules, "Assemblies", out var assemblies)
            || assemblies.ValueKind != JsonValueKind.Array)
            return;
        var index = 0;
        foreach (var item in assemblies.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                byIndex[index] = item.GetString() ?? "";
            index++;
        }
    }

    // Configuration keys are case-insensitive.
    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var p in element.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        return false;
    }
}

/// <summary>The candidate image's boot modules, as the combo gate activates them.</summary>
public sealed record ImageBootModuleSet
{
    /// <summary>Each boot module the gate activates, as its path inside the container.</summary>
    public ImmutableArray<string> Paths { get; init; } = [];

    /// <summary>Declared entries the combo also LANDS — activated from the landed copy only.</summary>
    public ImmutableArray<string> ShadowedByLanded { get; init; } = [];

    /// <summary>Declared entries the composed host does not carry. A portal that requires one
    /// would refuse readiness on this image; the gate names them and activates the rest.</summary>
    public ImmutableArray<string> Absent { get; init; } = [];

    /// <summary>The tester arguments: one <c>--module</c> per activated boot module.</summary>
    public ImmutableArray<string> TesterArgs() =>
        [.. Paths.SelectMany(p => new[] { "--module", p })];

    /// <summary>One line for the run log.</summary>
    public string Describe() =>
        $"image boot modules: {Paths.Length} activated"
        + (Paths.Length > 0 ? $" ({string.Join(", ", Paths.Select(Path.GetFileNameWithoutExtension))})" : "")
        + (ShadowedByLanded.Length > 0 ? $"; {ShadowedByLanded.Length} taken from the landed copy instead ({string.Join(", ", ShadowedByLanded)})" : "")
        + (Absent.Length > 0 ? $"; {Absent.Length} declared but NOT in the image ({string.Join(", ", Absent)})" : "");
}
