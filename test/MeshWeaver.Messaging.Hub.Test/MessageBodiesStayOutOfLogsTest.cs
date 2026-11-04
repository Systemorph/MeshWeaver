using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// A message BODY is node content, and node content may be sensitive (health data, personal
/// data). Logs travel further than the store — to Loki, and from Loki into filed GitHub issues —
/// so the message service's error and no-handler paths log the delivery SUMMARY only (type, id,
/// sender → target, state), never the body. Rendering bodies is an explicit deployment opt-in,
/// <see cref="MessageService.LogMessageBodiesKey"/>, default off.
/// See <c>Doc/Architecture/SensitiveContentInLogs</c>.
/// </summary>
public abstract class MessageBodiesLogTestBase : HubTestBase
{
    /// <summary>The string standing in for a sensitive field of node content.</summary>
    protected const string SensitiveMarker = "PATIENT-DIAGNOSIS-7f3a9c";

    protected sealed record SensitiveRequest(string Diagnosis) : IRequest<Ack>;

    /// <summary>The value of a member MARKED <see cref="SensitiveDataAttribute"/>.</summary>
    protected const string MarkedMarker = "MARKED-DIAGNOSIS-2d81e0";

    /// <summary>The value of an UNMARKED member of the same message (the control).</summary>
    protected const string WardMarker = "WARD-east-5c7a";

    /// <summary>A request with one marked and one unmarked member.</summary>
    protected sealed record PatientRequest([SensitiveData] string Diagnosis, string Ward) : IRequest<Ack>;

    /// <summary>A fire-and-forget message nobody handles (the no-handler path).</summary>
    protected sealed record UnhandledSensitiveEvent(string Diagnosis);

    protected sealed record Ack;

    protected readonly LogCapture Capture = new();

    protected MessageBodiesLogTestBase(ITestOutputHelper output, IDictionary<string, string?>? configuration)
        : base(output)
    {
        Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(Capture));
        if (configuration is not null)
            Services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder().AddInMemoryCollection(configuration).Build());
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(SensitiveRequest), typeof(UnhandledSensitiveEvent), typeof(Ack), typeof(PatientRequest))
            .WithHandler<SensitiveRequest>((_, _) => throw new InvalidOperationException("a genuine bug"))
            .WithHandler<PatientRequest>((_, _) => throw new InvalidOperationException("a patient bug"));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithTypes(typeof(SensitiveRequest), typeof(UnhandledSensitiveEvent), typeof(Ack), typeof(PatientRequest));

    /// <summary>Posts a request whose handler throws, and returns the message service's Error line.</summary>
    protected async Task<LogEntry> HandlerFaultErrorLine()
    {
        var client = GetClient();
        GetHost();
        using var _ = client
            .Observe<Ack>(new SensitiveRequest(SensitiveMarker), o => o.WithTarget(CreateHostAddress()))
            .Subscribe(_ => { }, _ => { });
        return await Capture.Entries.Should().Within(10.Seconds()).Match(
            e => e.Level == LogLevel.Error && e.Message.Contains("An exception occurred during the processing of"),
            "a handler fault is logged at Error by the message service");
    }

    /// <summary>Posts a <see cref="PatientRequest"/> whose handler throws, and returns its Error line.</summary>
    protected async Task<LogEntry> PatientFaultErrorLine()
    {
        var client = GetClient();
        GetHost();
        using var _ = client
            .Observe<Ack>(new PatientRequest(MarkedMarker, WardMarker), o => o.WithTarget(CreateHostAddress()))
            .Subscribe(_ => { }, _ => { });
        return await Capture.Entries.Should().Within(10.Seconds()).Match(
            e => e.Level == LogLevel.Error && e.Message.Contains("An exception occurred during the processing of")
                 && e.Message.Contains(nameof(PatientRequest)),
            "a handler fault is logged at Error by the message service");
    }

    /// <summary>Posts a message no handler matches, and returns the failure Warning naming it.</summary>
    protected async Task<LogEntry> NoHandlerWarningLine()
    {
        var client = GetClient();
        GetHost();
        client.Post(new UnhandledSensitiveEvent(SensitiveMarker), o => o.WithTarget(CreateHostAddress()));
        return await Capture.Entries.Should().Within(10.Seconds()).Match(
            e => e.Level == LogLevel.Warning && e.Message.Contains("No handler found for delivery"),
            "an unhandled message is reported as a delivery failure, at Warning");
    }

    protected sealed record LogEntry(string Category, LogLevel Level, string Message);

    protected sealed class LogCapture : ILoggerProvider
    {
        public ReplaySubject<LogEntry> Entries { get; } = new();
        private readonly ConcurrentQueue<LogEntry> logged = new();
        public IReadOnlyCollection<LogEntry> Logged => logged.ToArray();
        public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, this);
        public void Dispose() { }

        private void Record(LogEntry entry)
        {
            logged.Enqueue(entry);
            Entries.OnNext(entry);
        }

        private sealed class Capturing(string category, LogCapture sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                    sink.Record(new LogEntry(category, logLevel, formatter(state, exception)));
            }
        }
    }
}

/// <summary>Default deployment: no opt-in, so no body reaches a log line.</summary>
public class MessageBodiesStayOutOfLogsTest(ITestOutputHelper output)
    : MessageBodiesLogTestBase(output, configuration: null)
{
    [HubFact]
    public async Task A_handler_fault_logs_the_summary_not_the_body()
    {
        var line = await HandlerFaultErrorLine();
        line.Message.Should().Contain(nameof(SensitiveRequest),
            "the summary still names the message type, so the fault stays diagnosable");
        line.Message.Should().NotContain(SensitiveMarker,
            "a message body is node content and may be sensitive: the Error line carries the summary only");
        line.Message.Should().Contain("a genuine bug", "the exception itself is still logged");
    }

    [HubFact]
    public async Task A_no_handler_failure_does_not_render_the_message()
    {
        var line = await NoHandlerWarningLine();
        line.Message.Should().Contain(nameof(UnhandledSensitiveEvent));
        line.Message.Should().NotContain(SensitiveMarker,
            "the failure text used to append the record's ToString(), which prints every field");
    }

    [HubFact]
    public async Task No_warning_or_error_line_carries_the_body()
    {
        await HandlerFaultErrorLine();
        await NoHandlerWarningLine();
        Capture.Logged.Where(e => e.Message.Contains(SensitiveMarker)).Should().BeEmpty("no Warning-or-above line may carry a message body by default");
    }
}

/// <summary>A deployment that opted in with <c>Messaging:LogMessageBodies=true</c> gets the body.</summary>
public class MessageBodiesOptInTest(ITestOutputHelper output)
    : MessageBodiesLogTestBase(output, new Dictionary<string, string?>
    {
        [MessageService.LogMessageBodiesKey] = "true",
    })
{
    [HubFact]
    public async Task The_opt_in_renders_the_body_on_a_handler_fault()
    {
        var line = await HandlerFaultErrorLine();
        line.Message.Should().Contain(SensitiveMarker,
            "Messaging:LogMessageBodies=true is the explicit opt-in to body rendering");
    }

    /// <summary>
    /// 🚨 The opt-in renders the body — and still never a <see cref="SensitiveDataAttribute"/> value:
    /// the body goes through the logging serializer, the one redaction point.
    /// </summary>
    [HubFact]
    public async Task The_opt_in_still_redacts_a_marked_member()
    {
        var line = await PatientFaultErrorLine();
        line.Message.Should().Contain(WardMarker, "CONTROL — the opt-in does render the body");
        line.Message.Should().NotContain(MarkedMarker, "a [SensitiveData] value never reaches a log, opt-in or not");
        line.Message.Should().Contain(SensitiveDataAttribute.Placeholder, "the member is shown, its value replaced");
    }
}
