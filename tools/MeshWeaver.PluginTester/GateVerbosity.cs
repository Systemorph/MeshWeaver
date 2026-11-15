using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginTester;

/// <summary>
/// ONE switch for how much the tester says: <c>MW_LOG_LEVEL</c>. Unset (the CI default) means the
/// bake and the gate print their VERDICTS — per package, per NodeType — plus warnings and errors;
/// every per-node progress line, every compiler warning of a type that compiled, and the per-hub
/// [QUIESCE-*] attribution pairs are held back. Maintainer, 2026-09-13 ("logs are super verbose",
/// "build output in bake gates still on info"): a full-run shard wrote 16,000 lines, of which the
/// verdict was 93. <c>MW_LOG_LEVEL=Information</c> (or lower) restores all of it; the /debug skill's
/// Trace shape is unchanged.
/// </summary>
internal static class GateVerbosity
{
    public static readonly LogLevel MinLevel = Enum.TryParse<LogLevel>(
        Environment.GetEnvironmentVariable("MW_LOG_LEVEL"), ignoreCase: true, out var lvl) ? lvl : LogLevel.Warning;

    /// <summary>True when MW_LOG_LEVEL asks for Information or lower — the chatty shape.</summary>
    public static bool Verbose => MinLevel <= LogLevel.Information;

    /// <summary>
    /// The per-category filters <c>MW_LOG_CATEGORIES</c> asks for: a comma-separated list of logger
    /// categories, each optionally suffixed <c>=Level</c> (<c>bake-seed=Debug</c>).
    ///
    /// <para>🚨 A named category is logged at the level it names, or at Information when it names
    /// none — and never less verbosely than <paramref name="minLevel"/>. It used to be filtered at
    /// <paramref name="minLevel"/> itself, so at the default Warning naming a category raised
    /// nothing: the switch did something only under <c>MW_LOG_LEVEL=Trace</c>, which turns the
    /// whole run chatty. The combo gate needs a handful of adoption categories without that
    /// (MeshWeaver#4413), and one of their lines (the seeder's "NOT re-stamped") is Debug.</para>
    /// </summary>
    /// <param name="raw">The variable's value, or null when unset.</param>
    /// <param name="minLevel">The run's global minimum (<see cref="MinLevel"/>).</param>
    /// <exception cref="ArgumentException">An entry names a level that is not a <see cref="LogLevel"/>.</exception>
    public static IReadOnlyList<(string Category, LogLevel Level)> CategoryFilters(string? raw, LogLevel minLevel)
    {
        var filters = new List<(string, LogLevel)>();
        foreach (var entry in (raw ?? "").Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = entry.IndexOf('=');
            var category = (eq < 0 ? entry : entry[..eq]).Trim();
            if (category.Length == 0)
                continue;
            var level = LogLevel.Information;
            if (eq >= 0)
            {
                var named = entry[(eq + 1)..].Trim();
                if (!Enum.TryParse(named, ignoreCase: true, out level) || !Enum.IsDefined(level))
                    throw new ArgumentException(
                        $"MW_LOG_CATEGORIES entry '{entry}' names level '{named}', which is not a "
                        + $"LogLevel ({string.Join(", ", Enum.GetNames<LogLevel>())}).");
            }
            filters.Add((category, level < minLevel ? level : minLevel));
        }
        return filters;
    }
}
