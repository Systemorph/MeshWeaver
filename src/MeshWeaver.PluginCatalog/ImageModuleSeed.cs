using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Plugin.Packaging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// What the IMAGE's own copy of a module was built as — the package version and content hash its
/// <c>manifest.lock</c> stated at the commit the image was built from — read from the
/// <see cref="FileName"/> stamp the closure lane (<c>memex/MeshModulesPublish.targets</c>,
/// <c>WriteMeshModuleSeedStamps</c>) writes beside the module's DLL under the image's
/// <c>modules/&lt;Name&gt;/</c> (MeshWeaver#6044).
///
/// <para>🚨 <b>Why it exists.</b> Boot decides between the image's copy of a module and a store
/// copy of the same name (<see cref="ModuleActivationBoot"/>, pass 1). Until this stamp the only
/// discriminator was the FRAMEWORK identity (#4161), and under the compatibility key every build
/// of one platform epoch states the same one — so a store copy built from OLDER sources than the
/// image won simply by being a store copy. Measured on memex.meshweaver.cloud 2026-09-28: an image
/// built from Plugins <c>8931656f</c> ran <c>MeshWeaver.AI</c> 1.16.3 built from <c>6d21172e</c>,
/// because that store copy stated the same framework identity as the image. The module DLL itself
/// carries no package version (its informational version is the PLATFORM's, MeshWeaver#3732), so
/// the fact has to be written down when the image is built — this file is that record.</para>
///
/// <para>🚨 <b>Absent states nothing.</b> An image built before the stamp existed, a module whose
/// package the build could not find, or an unreadable file all read as <c>null</c>, and a
/// <c>null</c> decides nothing: the boot rule is then exactly what it was before (rule R2 of
/// <c>Doc/Architecture/ModuleAdoptionPolicy</c> — absence of evidence is not evidence).</para>
/// </summary>
/// <param name="Module">The module's assembly simple name (<c>MeshWeaver.AI</c>).</param>
/// <param name="Package">The package that owns it (<c>AI</c>), when the stamp names one.</param>
/// <param name="Version">The package's released version as its <c>manifest.lock</c> stated it
/// when the image was built, or null.</param>
/// <param name="ModuleVersion">The <c>manifest.lock</c>'s content hash at that commit, or null —
/// reported, never compared: a store entry does not record one.</param>
public sealed record ImageModuleSeed(string Module, string? Package, string? Version, string? ModuleVersion)
{
    /// <summary>The stamp's file name inside the image's <c>modules/&lt;Name&gt;/</c> folder. The
    /// closure lane writes exactly this name; renaming it on one side only disarms the rule.</summary>
    public const string FileName = "module.seed.json";

    /// <summary>The stamp's schema marker.</summary>
    public const string Schema = "mw-module-seed/1";

    /// <summary>
    /// The stamp beside the IMAGE's copy of <paramref name="moduleName"/> — resolved through
    /// <see cref="MeshBuilder.ResolveModulePath(string, string?)"/> with NO landed root, i.e. the
    /// image's own <c>modules/&lt;Name&gt;/</c> or app closure, never the deployment's landed tree
    /// (a landed generation is the OTHER copy this stamp is compared against). Null when the image
    /// ships no stamp for it.
    /// </summary>
    /// <param name="moduleName">The module's assembly simple name.</param>
    public static ImageModuleSeed? OfImageCopy(string moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
            return null;
        var dll = MeshBuilder.ResolveModulePath(moduleName + ".dll", null);
        var directory = Path.GetDirectoryName(dll);
        return string.IsNullOrEmpty(directory) ? null : Read(directory, moduleName);
    }

    /// <summary>
    /// Reads the stamp in <paramref name="moduleDirectory"/> for <paramref name="moduleName"/>.
    /// Null when the file is absent, unreadable, or names a different module — a stamp that
    /// describes another module (a stray copy, an app-closure folder shared by several) must never
    /// decide for this one.
    /// </summary>
    /// <param name="moduleDirectory">The folder holding the module's DLL.</param>
    /// <param name="moduleName">The module's assembly simple name.</param>
    public static ImageModuleSeed? Read(string moduleDirectory, string moduleName)
    {
        var path = Path.Combine(moduleDirectory, FileName);
        if (!File.Exists(path))
            return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var module = Text(root, "module");
            if (!string.Equals(module, moduleName, StringComparison.OrdinalIgnoreCase))
                return null;
            return new ImageModuleSeed(module!, Text(root, "package"), Text(root, "version"),
                Text(root, "moduleVersion"));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable states nothing — the boot rule falls back to what it was before the stamp.
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
            ? text.Trim()
            : null;

    /// <summary>
    /// 🚨 THE rule (MeshWeaver#6044): why a store copy of an image-shipped module must NOT override
    /// the image's copy, or null when it may.
    ///
    /// <para>A store copy overrides the image's copy only when it is a STRICTLY NEWER release of
    /// the package: its recorded <see cref="ModuleActivationEntry.Version"/> ranks above the
    /// <paramref name="seed"/>'s. An EQUAL version is not newer — and the equal case is the one
    /// that happened: an unsettled <c>manifest.lock</c> keeps the previous version while the sources
    /// move on, so an image built from that tree carries NEWER sources under the SAME number as the
    /// last published bundle. Publication only follows a settled lock, which bumps the version, so
    /// a store copy with the image's own version can be the same sources or older ones, never newer
    /// ones; and the image's copy was compiled against this very platform. Preferring it is
    /// therefore right in every equal case there is.</para>
    ///
    /// <para>Decides nothing — returns null — when either side states no ordered version: no stamp
    /// (an image from before the stamp), a stamp with no version, or a store entry that recorded
    /// none. Rule R2: an unrecorded version is absence of evidence, and reading it as "older" would
    /// decline every module landed before versions were recorded.</para>
    /// </summary>
    /// <param name="entry">The store-installed entry that would override the image's copy.</param>
    /// <param name="seed">The image's stamp for the same module, or null.</param>
    public static string? DeclineReason(ModuleActivationEntry entry, ImageModuleSeed? seed)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (seed?.Version is not { } imageVersion || !IsOrderedVersion(imageVersion)
            || entry.Version is not { } storeVersion || !IsOrderedVersion(storeVersion))
            return null;
        if (NuGetVersionComparer.Instance.Compare(storeVersion, imageVersion) > 0)
            return null;
        var relation = NuGetVersionComparer.Instance.Compare(storeVersion, imageVersion) == 0
            ? "the SAME version as"
            : "OLDER than";
        return $"declined: its version {storeVersion} is {relation} the image's own copy "
               + $"({seed.Package ?? "package"}@{imageVersion}, stamped in {FileName}) — the image's copy runs "
               + "instead. A store copy overrides an image-shipped module only when it is a strictly "
               + "newer release (MeshWeaver#6044): an equal version can carry older sources than the "
               + "image (an unsettled manifest.lock keeps the number while the sources move), never "
               + "newer ones. No action is needed: the next published release of this package lands "
               + "and wins.";
    }

    /// <summary>True for <c>MAJOR[.MINOR[.PATCH]]</c> with an optional <c>-pre</c> /
    /// <c>+meta</c> tail — the shapes <see cref="NuGetVersionComparer"/> orders meaningfully.</summary>
    private static bool IsOrderedVersion(string text)
    {
        var core = text.Trim();
        var cut = core.IndexOfAny(['-', '+']);
        if (cut >= 0)
            core = core[..cut];
        var parts = core.Split('.');
        return parts.Length is >= 1 and <= 4
               && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit));
    }
}
