using System.Collections.Immutable;
using MeshWeaver.GitSync;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The KNOWN DEBT a module's source repository declares for the gate, read from the same tree the
/// combo materialised the module from (MeshWeaver#4413).
///
/// <para>A node repository's own gate (<c>node-repo-gate.yml</c>, <c>mw-plugin-test --allow</c>)
/// tolerates the failures listed in <see cref="FileName"/> at the repository root: an executable
/// spec whose live rows are red BY DESIGN until the platform gaps they name land is listed there,
/// reported, and does not fail the repository's PR. The combo gate ran the same tester over the
/// same content WITHOUT that list, so every instance that installed such a package read
/// <see cref="ComboVerdictKind.Red"/> on every candidate — a verdict about debt the source already
/// declared, not about the candidate — and the lane could never produce a usable verdict for it.
/// This reads the list so the combo holds the module to the SAME standard its own repository's gate
/// does: a declared failure is reported as known debt; a failure the list does not name is still a
/// failure.</para>
///
/// <para>🚨 Only the source repository's ratchet is honoured, never one of the instance's: the file
/// is read at the commit the module was materialised from, so a debt entry the repository has since
/// deleted does not excuse an older install, and an entry no newer commit carries cannot be invented
/// here. Stale entries (a listed check that now passes) are the repository's own ratchet to fail;
/// the combo neither fails nor reports them, because a combo verdict is about the candidate.</para>
///
/// <para>The file format is the tester's (<c>GateAllowlist</c> in <c>mw-plugin-test</c>): one
/// <c>&lt;scope&gt; &lt;check&gt;</c> entry per line, optionally followed by
/// <c>intermittent</c>, <c>#</c> comments. Scope is a package folder (package-level checks) or a
/// NodeType path below it (type-level checks), both relative to the repository root.</para>
/// </summary>
internal static class ComboKnownDebt
{
    /// <summary>The known-debt file at a node repository's root — <c>node-repo-gate.yml</c>'s
    /// default <c>allow-file</c>. A repository that configures another path declares no debt the
    /// combo can see, which is the stricter reading.</summary>
    public const string FileName = "plugin-gate.allow";

    private static readonly ImmutableHashSet<string> Checks = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "install", "idempotence", "content", "compile", "render", "tests");

    /// <summary>One declared entry, its scope already rewritten from the repository's folder to the
    /// module's work-root folder (the path the gate reports).</summary>
    /// <param name="Scope">Package id or NodeType path, as the gate report names it.</param>
    /// <param name="Check">The check name, lower case.</param>
    public sealed record Entry(string Scope, string Check)
    {
        /// <summary>The entry as the allow file spells it.</summary>
        public override string ToString() => $"{Scope} {Check}";
    }

    /// <summary>
    /// The entries of <paramref name="snapshot"/>'s known-debt file that belong to the module
    /// materialised from <paramref name="folder"/> as <paramref name="moduleId"/>.
    /// Empty when the tree carries no such file.
    /// </summary>
    /// <param name="snapshot">The whole-repository snapshot the module's files were selected from.</param>
    /// <param name="folder">The module's folder in the repository.</param>
    /// <param name="moduleId">The module's folder under the combo work root.</param>
    /// <exception cref="FormatException">A malformed line — the caller reports it and honours
    /// nothing, never a guessed subset.</exception>
    public static ImmutableList<Entry> ForModule(RepoSnapshot snapshot, string folder, string moduleId)
    {
        var file = snapshot.Files.FirstOrDefault(f =>
            string.Equals(f.Path, FileName, StringComparison.Ordinal));
        if (file is null)
            return [];
        return Parse(file.Content)
            .Select(entry => Rewrite(entry, folder, moduleId))
            .OfType<Entry>()
            .ToImmutableList();
    }

    /// <summary>Parses the file's text; throws on a malformed line, naming it.</summary>
    /// <param name="content">The file's text.</param>
    internal static ImmutableList<Entry> Parse(string content)
    {
        var entries = ImmutableList.CreateBuilder<Entry>();
        var number = 0;
        foreach (var raw in content.ReplaceLineEndings("\n").Split('\n'))
        {
            number++;
            var hash = raw.IndexOf('#');
            var line = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (line.Length == 0)
                continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var intermittent = parts.Length == 3
                && string.Equals(parts[2], "intermittent", StringComparison.OrdinalIgnoreCase);
            if ((parts.Length != 2 && !intermittent) || !Checks.Contains(parts[1]))
                throw new FormatException(
                    $"{FileName} line {number}: expected '<scope> <check>' (optionally followed by "
                    + $"'intermittent') with check one of [{string.Join(", ", Checks.Order())}], "
                    + $"got '{line}'");
            entries.Add(new Entry(parts[0], parts[1].ToLowerInvariant()));
        }
        return entries.ToImmutable();
    }

    /// <summary>The entry re-rooted at the module's work-root folder, or null when it names a
    /// scope outside <paramref name="folder"/>.</summary>
    private static Entry? Rewrite(Entry entry, string folder, string moduleId)
    {
        if (string.Equals(entry.Scope, folder, StringComparison.OrdinalIgnoreCase))
            return entry with { Scope = moduleId };
        var prefix = folder + "/";
        return entry.Scope.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? entry with { Scope = moduleId + "/" + entry.Scope[prefix.Length..] }
            : null;
    }
}
