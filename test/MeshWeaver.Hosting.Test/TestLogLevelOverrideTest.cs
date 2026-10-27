using System.Collections.Concurrent;
using System.Collections.Generic;
using MeshWeaver.Fixture;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the shared test sinks' level rule (<see cref="TestLogLevel"/>, Doc/Architecture/TestLogLevels):
/// Warning and above by default, whatever configuration says, and an explicit
/// <c>MW_TEST_LOG_LEVEL</c> override that really LOWERS it.
///
/// <para>The override used to be fed through a floor (<c>max(configured, override)</c>), which can
/// only raise a level, and the <c>LoggerFactory</c>'s own category rules from the shared
/// <c>appsettings.json</c> (<c>"MeshWeaver": "Warning"</c>) filtered the lines before the sink was
/// asked at all — so <c>MW_TEST_LOG_LEVEL=Debug</c> produced no Debug line (Copilot on core #7285).
/// The pipeline cases below build exactly that configuration and read what the sink WROTE.</para>
/// </summary>
public class TestLogLevelOverrideTest
{
    [Theory]
    [InlineData(LogLevel.Trace, LogLevel.Warning)]
    [InlineData(LogLevel.Information, LogLevel.Warning)]
    [InlineData(LogLevel.Warning, LogLevel.Warning)]
    [InlineData(LogLevel.Error, LogLevel.Error)]
    [InlineData(LogLevel.None, LogLevel.None)]
    public void WithoutAnOverride_TheConfiguredLevelIsRaisedToWarning(LogLevel configured, LogLevel expected)
        => TestLogLevel.Resolve(configured, null).Should().Be(expected);

    [Theory]
    [InlineData(LogLevel.Warning, LogLevel.Debug)]
    [InlineData(LogLevel.Information, LogLevel.Debug)]
    [InlineData(LogLevel.Trace, LogLevel.Error)]
    public void AnExplicitOverride_ReplacesBothTheFloorAndTheConfiguredLevel(LogLevel configured, LogLevel @override)
        => TestLogLevel.Resolve(configured, @override).Should().Be(@override);

    [Theory]
    [InlineData("Debug", LogLevel.Debug)]
    [InlineData("information", LogLevel.Information)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("loud", null)]
    public void TheEnvironmentValue_ParsesCaseInsensitively_AndAnUnknownValueIsNoOverride(string? value, LogLevel? expected)
        => TestLogLevel.Parse(value).Should().Be(expected);

    [Fact]
    public void WithoutAnOverride_TheXUnitSink_WritesWarningButNotInformation()
    {
        var (factory, lines) = BuildXUnitSink(@override: null);
        var logger = factory.CreateLogger("MeshWeaver.Probe");

        logger.LogInformation("info-line");
        logger.LogWarning("warning-line");

        lines.Should().NotContain(l => l.Contains("info-line"));
        lines.Should().Contain(l => l.Contains("warning-line"));
    }

    [Fact]
    public void AnOverrideOfDebug_ReachesPastTheConfiguredCategoryRules_IntoTheXUnitSink()
    {
        var (factory, lines) = BuildXUnitSink(@override: LogLevel.Debug);
        var logger = factory.CreateLogger("MeshWeaver.Probe");

        logger.LogDebug("debug-line");
        logger.LogTrace("trace-line");

        lines.Should().Contain(l => l.Contains("debug-line"),
            "the override must beat both the sink's Warning floor and the factory's "
            + "\"MeshWeaver\": \"Warning\" rule");
        lines.Should().NotContain(l => l.Contains("trace-line"), "Trace is below the requested Debug");
    }

    [Fact]
    public void AnOverrideOfDebug_ReachesTheFileSinkProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(SharedTestConfiguration());
        services.AddLogging(logging => TestLogLevel.ApplyOverride(logging, LogLevel.Debug));
        using var provider = services.BuildServiceProvider();

        using var fileProvider = new XUnitFileLoggerProvider(() => null, provider);
        var logger = fileProvider.CreateLogger("MeshWeaver.Probe");

        logger.IsEnabled(LogLevel.Debug).Should().BeTrue(
            "the file sink honours the registered override over the configured Warning");
        logger.IsEnabled(LogLevel.Trace).Should().BeFalse();
    }

    [Fact]
    public void WithoutAnOverride_TheFileSinkKeepsTheWarningFloor()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Debug" })
            .Build());
        using var provider = services.BuildServiceProvider();

        using var fileProvider = new XUnitFileLoggerProvider(() => null, provider);
        var logger = fileProvider.CreateLogger("MeshWeaver.Probe");

        logger.IsEnabled(LogLevel.Information).Should().BeFalse("configuration cannot lower the floor");
        logger.IsEnabled(LogLevel.Warning).Should().BeTrue();
    }

    private static IConfiguration SharedTestConfiguration()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The shared test/appsettings.json, verbatim.
                ["Logging:LogLevel:Default"] = "Warning",
                ["Logging:LogLevel:Microsoft"] = "Warning",
                ["Logging:LogLevel:System"] = "Warning",
                ["Logging:LogLevel:MeshWeaver"] = "Warning",
            })
            .Build();

    private static (ILoggerFactory Factory, ConcurrentQueue<string> Lines) BuildXUnitSink(LogLevel? @override)
    {
        var lines = new ConcurrentQueue<string>();
        var accessor = new TestOutputHelperAccessor { OutputHelper = new CapturingOutput(lines) };
        var configuration = SharedTestConfiguration();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.AddXUnitLogger(accessor);
            // AddXUnitLogger applied the process's MW_TEST_LOG_LEVEL; the explicit argument here
            // makes the case independent of the developer's shell.
            TestLogLevel.ApplyOverride(logging, @override);
        });
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<ILoggerFactory>(), lines);
    }

    private sealed class CapturingOutput(ConcurrentQueue<string> lines) : ITestOutputHelper
    {
        public string Output => string.Join("\n", lines);
        public void Write(string message) => lines.Enqueue(message);
        public void Write(string format, params object[] args) => lines.Enqueue(string.Format(format, args));
        public void WriteLine(string message) => lines.Enqueue(message);
        public void WriteLine(string format, params object[] args) => lines.Enqueue(string.Format(format, args));
    }
}
