using Microsoft.Extensions.Logging;

namespace MeshWeaver.Fixture;

/// <summary>
/// The ONE level rule for the shared test-log sinks (<see cref="XUnitLogger"/> and
/// <see cref="XUnitFileLogger"/>): test hosts write <see cref="LogLevel.Warning"/> and above, and
/// nothing below it — no category allow-list, whatever a test's <c>appsettings.json</c> or a host's
/// <c>SetMinimumLevel</c> says (maintainer, 2026-10-26: "unit and CI test runs: warning only").
///
/// <para><b>Why a floor and not a default.</b> Both sinks resolved their level from configuration
/// and fell back to Information when a host had none — and an Orleans <c>TestCluster</c> silo host
/// has none, so every silo logged at Information into the test output. Measured on core CI run
/// 40411029765 (shard 0): 18,434 <c>info,</c> lines of a 39,050-line job log, against 56 at
/// Warning or above — <c>Workspace content-type</c> registration, <c>[QUIESCE-START]</c> /
/// <c>[QUIESCE-OK]</c> per hub disposal, <c>Orleans.Runtime.SiloOptionsLogger</c>. A default can be
/// overridden by any of the dozens of places a test host is composed; a floor in the sink cannot.</para>
///
/// <para><b>A test that asserts on a lower-level line</b> does not go through these sinks: it adds
/// its own capture provider (an <c>ILoggerProvider</c> it owns, with its own
/// <c>AddFilter&lt;TProvider&gt;</c>), which this floor does not touch. The fault sink
/// (<c>TestTraceLog.AppendFault</c>) is Warning-and-above by construction and is unaffected.</para>
///
/// <para>To read Information or Debug lines while debugging locally, set the environment variable
/// <c>MW_TEST_LOG_LEVEL</c> (e.g. <c>Debug</c>) — never in CI, never in a committed file.</para>
/// </summary>
public static class TestLogLevel
{
    /// <summary>The lowest level a shared test sink writes.</summary>
    public static readonly LogLevel Minimum =
        System.Enum.TryParse<LogLevel>(
            System.Environment.GetEnvironmentVariable("MW_TEST_LOG_LEVEL"), ignoreCase: true, out var level)
            ? level
            : LogLevel.Warning;

    /// <summary>Raises <paramref name="configured"/> to <see cref="Minimum"/> when it is lower.</summary>
    /// <param name="configured">The level the sink resolved from configuration.</param>
    /// <returns>The effective level.</returns>
    public static LogLevel Floor(LogLevel configured) => configured < Minimum ? Minimum : configured;
}
