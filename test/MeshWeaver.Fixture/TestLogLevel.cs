using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
/// <para><b>Reading Information or Debug locally.</b> Set the environment variable
/// <c>MW_TEST_LOG_LEVEL</c> (e.g. <c>Debug</c>) — never in CI, never in a committed file. That
/// explicit setting REPLACES the floor and the configured level alike (<see cref="Resolve(LogLevel, LogLevel?)"/>),
/// and <see cref="ApplyOverride(ILoggingBuilder, LogLevel?)"/> adds a provider-specific factory rule
/// for both sinks, so the <c>LoggerFactory</c>'s own category rules (the shared
/// <c>appsettings.json</c>'s <c>"MeshWeaver": "Warning"</c>) cannot filter the lines out before
/// they reach the sink. A floor alone could only ever RAISE the level, which made the documented
/// switch a no-op.</para>
/// </summary>
public static class TestLogLevel
{
    /// <summary>The lowest level a shared test sink writes when no override is set.</summary>
    public const LogLevel Floor = LogLevel.Warning;

    /// <summary>
    /// The explicit local override from <c>MW_TEST_LOG_LEVEL</c>, or <c>null</c> when it is unset
    /// or does not parse as a <see cref="LogLevel"/>.
    /// </summary>
    public static readonly LogLevel? Override = Parse(System.Environment.GetEnvironmentVariable("MW_TEST_LOG_LEVEL"));

    /// <summary>
    /// Parses an override value; <c>null</c> for an empty or unknown value. <c>Enum.TryParse</c>
    /// alone accepts any integer (<c>999</c> parses), and an undefined level above
    /// <see cref="LogLevel.Critical"/> would silently disable even Error output — so the parsed value
    /// must also be a DEFINED <see cref="LogLevel"/>.
    /// </summary>
    /// <param name="value">The raw environment value.</param>
    /// <returns>The level, or <c>null</c>.</returns>
    public static LogLevel? Parse(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && System.Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level)
           && System.Enum.IsDefined(level)
            ? level
            : null;

    /// <summary>
    /// The effective level of a shared sink: the explicit <paramref name="override"/> when one is
    /// set (in either direction), otherwise <paramref name="configured"/> raised to <see cref="Floor"/>.
    /// </summary>
    /// <param name="configured">The level the sink resolved from configuration.</param>
    /// <param name="override">The explicit local override, or <c>null</c>.</param>
    /// <returns>The effective level.</returns>
    public static LogLevel Resolve(LogLevel configured, LogLevel? @override)
        => @override ?? (configured < Floor ? Floor : configured);

    /// <summary>Applies the process's <see cref="Override"/> to the factory's filter rules.</summary>
    /// <param name="builder">The logging builder the shared sinks are registered on.</param>
    /// <returns><paramref name="builder"/>.</returns>
    public static ILoggingBuilder ApplyOverride(this ILoggingBuilder builder) => ApplyOverride(builder, Override);

    /// <summary>
    /// When <paramref name="override"/> is set, adds a category-less filter rule for each shared
    /// sink's provider. A provider-specific rule beats every provider-less rule in the
    /// <c>LoggerFactory</c>'s rule selection, so configuration's category rules no longer cut the
    /// lines off before the sink sees them. Without an override this adds nothing.
    /// </summary>
    /// <param name="builder">The logging builder the shared sinks are registered on.</param>
    /// <param name="override">The explicit local override, or <c>null</c>.</param>
    /// <returns><paramref name="builder"/>.</returns>
    public static ILoggingBuilder ApplyOverride(ILoggingBuilder builder, LogLevel? @override)
    {
        if (@override is { } level)
        {
            builder.AddFilter<XUnitLoggerProvider>(null, level);
            builder.AddFilter<XUnitFileLoggerProvider>(null, level);
            builder.Services.Replace(ServiceDescriptor.Singleton(new TestLogLevelOverride(level)));
        }
        return builder;
    }
}

/// <summary>
/// The explicit level override a container's shared test sinks honour, registered ONLY by
/// <see cref="TestLogLevel.ApplyOverride(ILoggingBuilder, LogLevel?)"/>. A provider without one
/// applies the Warning floor.
/// </summary>
/// <param name="Level">The level that replaces both the floor and the configured level.</param>
public sealed record TestLogLevelOverride(LogLevel Level);
