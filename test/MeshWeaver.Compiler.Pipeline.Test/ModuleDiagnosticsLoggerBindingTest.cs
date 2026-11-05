using MeshWeaver.Mesh;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The module diagnostics resolve no logger factory: at boot it can be the service under construction, at
/// teardown the root no longer answers (MeshWeaver.Plugins#4964). So what they write is HELD until the root is
/// bound. These pin the two ways that could go quiet: a warning reported once being spent before the binding, and
/// a host that never reaches the binding at all.
/// </summary>
public sealed class ModuleDiagnosticsLoggerBindingTest
{
    private const string Category = "MeshWeaver.Test.Deferred";

    private sealed class Recorder : ILogger, ILoggerProvider, ILoggerFactory
    {
        public List<string> Lines { get; } = [];
        public List<string> Categories { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
        public ILogger CreateLogger(string categoryName)
        {
            Categories.Add(categoryName);
            return this;
        }
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    [Fact]
    public void WhatIsWrittenBeforeTheBinding_IsFlushedInOrder_AndThenForwarded()
    {
        var owners = new ModuleInstanceOwners();
        var logger = owners.LoggerFor(Category);
        logger.LogWarning("first");
        logger.LogError("second");
        var recorder = new Recorder();

        owners.UseLoggerFactory(recorder);
        logger.LogWarning("third");

        recorder.Lines.Should().Equal("first", "second", "third");
        recorder.Categories.Should().Contain(Category);
    }

    [Fact]
    public void ALoggerTakenAfterTheBinding_WritesAtOnce()
    {
        var owners = new ModuleInstanceOwners();
        var recorder = new Recorder();
        owners.UseLoggerFactory(recorder);

        owners.LoggerFor(Category).LogWarning("late");

        recorder.Lines.Should().Equal("late");
    }

    [Fact]
    public void AHostThatNeverBinds_HoldsABoundedNumberOfEntries()
    {
        var deferred = new DeferredLogger(Category);
        for (var i = 0; i < DeferredLogger.Capacity + 10; i++)
            deferred.LogWarning("entry {Index}", i);
        var recorder = new Recorder();

        deferred.Bind(recorder);

        recorder.Lines.Should().HaveCount(DeferredLogger.Capacity);
        recorder.Lines[0].Should().Be("entry 0");
    }

    [Fact]
    public void TheFirstBindingWins_SoASecondHostStartFlushesNothingTwice()
    {
        var deferred = new DeferredLogger(Category);
        deferred.LogWarning("once");
        var first = new Recorder();
        var second = new Recorder();

        deferred.Bind(first);
        deferred.Bind(second);

        first.Lines.Should().Equal("once");
        second.Lines.Should().BeEmpty();
    }

    /// <summary>The plain Microsoft DI path: no SetupModules ran, only the generic host's start.</summary>
    [Fact]
    public async Task OnAPlainMicrosoftDiHost_TheHostStartBindsTheDiagnostics()
    {
        var owners = new ModuleInstanceOwners();
        var recorder = new Recorder();
        using var provider = new ServiceCollection()
            .AddSingleton(owners)
            .AddSingleton<ILoggerFactory>(recorder)
            .BuildServiceProvider();
        owners.Logger.LogWarning("held until the host starts");
        recorder.Lines.Should().BeEmpty();

        await new ModuleLoggingBinding(provider).StartAsync(TestContext.Current.CancellationToken);

        recorder.Lines.Should().Equal("held until the host starts");
    }
}
