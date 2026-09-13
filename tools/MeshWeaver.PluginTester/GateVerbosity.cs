using System;
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
}
