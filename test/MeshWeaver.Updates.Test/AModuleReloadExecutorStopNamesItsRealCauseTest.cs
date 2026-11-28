using System.Reflection;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Updates.Test;

/// <summary>
/// A reload executor whose hub is torn down mid-step (a restart) ends with the hub's disposal
/// wrapped in a <see cref="TargetInvocationException"/> by the reflective reduce dispatch. It used to
/// log that wrapper at Error (an unclassified defect); the cause is unwrapped now, a disposal is
/// Information, and a genuine fault stays an Error naming the REAL exception.
/// </summary>
public class AModuleReloadExecutorStopNamesItsRealCauseTest
{
    private static readonly Address Hub = new("Admin", "_ModuleReload/auto-1");

    private sealed class Recorder : ILogger
    {
        public readonly List<(LogLevel Level, Exception? Ex)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, exception));
    }

    /// <summary>Each control-plane executor reports its stop through the one shared helper.</summary>
    public static IEnumerable<object[]> Executors() =>
    [
        [(Action<ILogger?, Exception, Address, bool>)ModuleReloadExecutor.LogStopped, "[ModuleReload]"],
        [(Action<ILogger?, Exception, Address, bool>)PackageUninstallExecutor.LogStopped, "[PackageUninstall]"],
        [(Action<ILogger?, Exception, Address, bool>)InstanceRebootExecutor.LogStopped, "[Reboot]"],
    ];

    [Theory]
    [MemberData(nameof(Executors))]
    public void EveryExecutorUnwrapsAndClassifiesItsStop(Action<ILogger?, Exception, Address, bool> logStopped, string _)
    {
        var disposal = new Recorder();
        logStopped(disposal, new TargetInvocationException(new HubDisposingException(Hub, "/MeshNode")), Hub, false);
        Assert.Equal(LogLevel.Information, Assert.Single(disposal.Entries).Level);

        var fault = new Recorder();
        var real = new InvalidOperationException("boom");
        logStopped(fault, new TargetInvocationException(real), Hub, false);
        var entry = Assert.Single(fault.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(real, entry.Ex);
    }

    [Fact]
    public void AWrappedDisposalOfItsOwnHubIsNotAnError()
    {
        var log = new Recorder();
        ModuleReloadExecutor.LogStopped(log,
            new TargetInvocationException(new HubDisposingException(Hub, "/MeshNode")), Hub);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
    }

    [Fact]
    public void AWrappedRealFaultIsAnErrorNamingTheInnerException()
    {
        var log = new Recorder();
        var real = new InvalidOperationException("boom");
        ModuleReloadExecutor.LogStopped(log, new TargetInvocationException(new TargetInvocationException(real)), Hub);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(real, entry.Ex);
    }

    private static ObjectDisposedException DisposedContainer() =>
        new(HubDisposingException.DisposedContainerObjectName);

    [Theory]
    [MemberData(nameof(Executors))]
    public void ADisposedContainerIsQuietOnlyWhenTheExecutorsOwnHubIsShuttingDown(
        Action<ILogger?, Exception, Address, bool> logStopped, string _)
    {
        var foreign = new Recorder();
        logStopped(foreign, DisposedContainer(), Hub, false);
        Assert.Equal(LogLevel.Error, Assert.Single(foreign.Entries).Level);

        var own = new Recorder();
        logStopped(own, DisposedContainer(), Hub, true);
        Assert.Equal(LogLevel.Information, Assert.Single(own.Entries).Level);
    }

    [Fact]
    public void ADifferentHubsDisposalStaysAnError()
    {
        var log = new Recorder();
        ModuleReloadExecutor.LogStopped(log,
            new TargetInvocationException(new HubDisposingException(new Address("Other", "x"), "/MeshNode")), Hub);
        Assert.Equal(LogLevel.Error, Assert.Single(log.Entries).Level);
    }
}
