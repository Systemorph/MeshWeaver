using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The boot registration window, one layer down (MeshWeaver#7318).</b> Measured on
/// memex-cloud: every pod writes <c>Received '$type':'SocialPost' which is NOT registered in this
/// (receiving) hub</c> — and the same line for ~90 other runtime-compiled content types — about a
/// second before <c>Application started</c>, once per pod, on every roll. The read seams'
/// "stayed an untyped JsonElement" verdict has been deferred to the settle of the registration
/// pass since Plugins#2799; the converter beneath them still wrote its own verdict at the read,
/// before the pass had registered the type, so the line asserted a missing <c>WithType</c> that
/// does not exist and was then cured by the pass minutes later.
///
/// <para>What is pinned: while the window is open the converter writes NOTHING at the read; a
/// type that registers before the settle is never warned, and one still unresolved is warned once,
/// at the settle, by the converter that deferred it. The negative control is the same read with no
/// window: it warns at the read, as it always did — which is also every host that does not run the
/// registration pass.</para>
///
/// <para>No mocking: the real converter, a real <see cref="MeshContentTypeRegistry"/> and the real
/// hosted service driven through its own <c>ApplicationStarted</c> kick.</para>
/// </summary>
public class BootRegistrationWindowDefersConverterWarningTest
{
    /// <summary>Stand-in for a runtime-compiled NodeType's content type (the SocialPost shape).</summary>
    public record ConverterWindowProbePost(string Title);

    private const string ProbeNodeType = "Probe/ConverterWindowPost";

    private const string Json = """{"$type":"ConverterWindowProbePost","title":"read at boot"}""";

    private static object? Read(ILogger<ObjectPolymorphicConverter> logger, IMeshContentTypeRegistry registry)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new ObjectPolymorphicConverter(
            MessageHubExtensions.CreateTypeRegistry(), logger, contentTypeRegistry: registry));
        return JsonSerializer.Deserialize<object>(Json, options);
    }

    private static IEnumerable<Record> Unregistered(RecordingLogger logger) =>
        logger.Records.Where(r => r.Level >= LogLevel.Warning
                                  && r.Message.Contains("NOT registered in this (receiving) hub", StringComparison.Ordinal));

    private static (DynamicContentTypeRegistrationHostedService Service, StartableLifetime Lifetime, Microsoft.Extensions.DependencyInjection.ServiceProvider Services)
        HostedPass(MeshContentTypeRegistry registry)
    {
        var lifetime = new StartableLifetime();
        var services = new ServiceCollection()
            .AddSingleton(new ContentDegradationRegistry())
            .AddSingleton<IMeshContentTypeRegistry>(registry)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // The "pass disabled" terminal settles at once on the kick — the settle is
                    // the subject here, not the pass.
                    [DynamicContentTypeRegistrationHostedService.EnabledConfigKey] = "false",
                })
                .Build())
            .BuildServiceProvider();
        var service = new DynamicContentTypeRegistrationHostedService(
            services, lifetime, new RecordingLogger<DynamicContentTypeRegistrationHostedService>());
        Assert.True(service.StartAsync(TestContext.Current.CancellationToken).IsCompletedSuccessfully);
        return (service, lifetime, services);
    }

    /// <summary>Negative control — no window: the converter warns at the read, once per type.</summary>
    [Fact]
    public void WithoutAWindow_TheConverterWarnsAtTheRead()
    {
        var logger = new RecordingLogger<ObjectPolymorphicConverter>();

        var value = Read(logger, new MeshContentTypeRegistry());

        Assert.IsType<JsonElement>(value);
        Assert.Single(Unregistered(logger));
    }

    /// <summary>
    /// The production shape: a boot-time reader touches the content before the pass registers its
    /// type; the pass then registers it and the window settles. Nothing is ever warned — the type
    /// was never missing from the receiving hub, only not compiled on this replica yet.
    /// </summary>
    [Fact]
    public void DuringTheWindow_ATypeThatThenRegisters_IsNeverWarned()
    {
        var registry = new MeshContentTypeRegistry();
        var (service, lifetime, services) = HostedPass(registry);
        using var _ = service;
        using var __ = services;
        var logger = new RecordingLogger<ObjectPolymorphicConverter>();

        Assert.IsType<JsonElement>(Read(logger, registry));
        Assert.Empty(Unregistered(logger));

        registry.Register(typeof(ConverterWindowProbePost), ProbeNodeType);
        lifetime.Start();

        Assert.Empty(Unregistered(logger));
        // The cure is real: the same read now types.
        Assert.IsType<ConverterWindowProbePost>(Read(new RecordingLogger<ObjectPolymorphicConverter>(), registry));
    }

    /// <summary>
    /// The deferral re-times the verdict and never drops it: a type still unresolved when the
    /// window settles is warned ONCE, by the converter that read it — and after the settle a read
    /// warns at the read again.
    /// </summary>
    [Fact]
    public void ATypeStillUnresolvedWhenTheWindowSettles_IsWarnedOnce_AtTheSettle()
    {
        var registry = new MeshContentTypeRegistry();
        var (service, lifetime, services) = HostedPass(registry);
        using var _ = service;
        using var __ = services;
        var logger = new RecordingLogger<ObjectPolymorphicConverter>();

        Read(logger, registry);
        Assert.Empty(Unregistered(logger));

        lifetime.Start();

        Assert.Single(Unregistered(logger));
        Assert.Equal(0, registry.SettleDeferredUnresolvedWarnings());

        var afterSettle = new RecordingLogger<ObjectPolymorphicConverter>();
        Read(afterSettle, registry);
        Assert.Single(Unregistered(afterSettle));
    }

    /// <summary>The window is bounded by what was deferred, and a name the registry refuses as
    /// ambiguous is still warned at the settle — ambiguity is a real verdict, not a boot race.</summary>
    [Fact]
    public void AnAmbiguousNameAtTheSettle_IsWarned()
    {
        var registry = new MeshContentTypeRegistry();
        registry.DeferUnresolvedWarningsUntilSettled();
        var warned = 0;
        Assert.True(registry.DeferUnresolvedWarning("Shared", () => Interlocked.Increment(ref warned)));

        registry.Register(typeof(ConverterWindowProbePost), "Probe/A");
        registry.Register(typeof(Ambiguous.ConverterWindowProbePost), "Probe/B");

        Assert.True(registry.DeferUnresolvedWarning(nameof(ConverterWindowProbePost), () => Interlocked.Increment(ref warned)));
        Assert.Equal(2, registry.SettleDeferredUnresolvedWarnings());
        Assert.Equal(2, warned);
        Assert.False(registry.DeferUnresolvedWarning("Shared", () => { }));
    }

    /// <summary>A closed generic: its FULL name (assembly-qualified type arguments) is far past the
    /// converter's 256-character display bound — a valid discriminator, not junk.</summary>
    public record LongNameProbe<T>(string Title);

    private static object? Read(ILogger<ObjectPolymorphicConverter> logger, IMeshContentTypeRegistry registry, string json)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new ObjectPolymorphicConverter(
            MessageHubExtensions.CreateTypeRegistry(), logger, contentTypeRegistry: registry));
        return JsonSerializer.Deserialize<object>(json, options);
    }

    private static IEnumerable<Record> Unbindable(RecordingLogger logger) =>
        logger.Records.Where(r => r.Level >= LogLevel.Warning
                                  && r.Message.Contains("could not be deserialized", StringComparison.Ordinal));

    /// <summary>
    /// A name that resolves mesh-wide but whose JSON does not bind (an object where the record
    /// declares a string) is a DATA problem, not a boot race: it is reported at the read, as
    /// unbindable, even while the window is open — the settle would find the name resolving and
    /// drop a deferred line, leaving the payload an untyped JsonElement with no diagnostic at all.
    /// </summary>
    [Fact]
    public void DuringTheWindow_AResolvableTypeWhoseJsonDoesNotBind_IsWarnedAtTheRead_AsUnbindable()
    {
        var registry = new MeshContentTypeRegistry();
        registry.Register(typeof(ConverterWindowProbePost), ProbeNodeType);
        var (service, lifetime, services) = HostedPass(registry);
        using var _ = service;
        using var __ = services;
        var logger = new RecordingLogger<ObjectPolymorphicConverter>();

        var value = Read(logger, registry, """{"$type":"ConverterWindowProbePost","title":{}}""");

        Assert.IsType<JsonElement>(value);
        Assert.Single(Unbindable(logger));
        Assert.Empty(Unregistered(logger));

        lifetime.Start();
        Assert.Single(Unbindable(logger));
        Assert.Empty(Unregistered(logger));
    }

    /// <summary>
    /// The settle looks up the ORIGINAL discriminator, never the truncated display text: a valid
    /// name longer than the display bound that registers before the settle is never warned.
    /// </summary>
    [Fact]
    public void DuringTheWindow_ALongNameThatThenRegisters_IsNeverWarned()
    {
        var longType = typeof(LongNameProbe<ImmutableDictionary<string, ImmutableList<string>>>);
        Assert.True(longType.FullName!.Length > 256, $"probe name is only {longType.FullName.Length} characters");
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["$type"] = longType.FullName,
            ["title"] = "read at boot",
        });
        var registry = new MeshContentTypeRegistry();
        var (service, lifetime, services) = HostedPass(registry);
        using var _ = service;
        using var __ = services;
        var logger = new RecordingLogger<ObjectPolymorphicConverter>();

        Assert.IsType<JsonElement>(Read(logger, registry, json));
        Assert.Empty(Unregistered(logger));

        registry.Register(longType, "Probe/LongName");
        lifetime.Start();

        Assert.Empty(Unregistered(logger));
        Assert.IsType(longType, Read(new RecordingLogger<ObjectPolymorphicConverter>(), registry, json));
    }

    private sealed record Record(LogLevel Level, string Message);

    private class RecordingLogger : ILogger
    {
        private ImmutableList<Record> records = ImmutableList<Record>.Empty;

        public IReadOnlyList<Record> Records => records;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                ImmutableInterlocked.Update(ref records, r => r.Add(new Record(logLevel, formatter(state, exception))));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class RecordingLogger<T> : RecordingLogger, ILogger<T>;

    /// <summary>The host lifetime's ApplicationStarted, fired on demand — hosting plumbing, not a
    /// mesh interface.</summary>
    private sealed class StartableLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource started = new();

        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void Start() => started.Cancel();

        public void StopApplication() { }
    }
}

/// <summary>A second declaration of the probe's short name, from another "package".</summary>
public static class Ambiguous
{
    /// <summary>Same short name as the probe, different declaration.</summary>
    public record ConverterWindowProbePost(string Title);
}
