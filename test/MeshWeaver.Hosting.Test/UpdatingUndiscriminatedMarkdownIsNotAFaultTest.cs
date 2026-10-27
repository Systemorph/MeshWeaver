using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 Systemorph/MeshWeaver#5736, measured END TO END: a <c>Markdown</c> node whose stored content
/// carries NO <c>$type</c> discriminator is updated through the real mesh, and the update lands
/// without the <c>NodeUpdatePipeline</c> filing <c>As&lt;MarkdownContent&gt; for {path} could not
/// recover value: JsonException</c> at Error.
///
/// <para><c>NodeUpdateContentTypeChangeTest</c> pins the seam (<c>WithExistingContentTyped</c>) as
/// a pure function. This test pins the PATH production took: <c>IMeshService.UpdateNode</c> →
/// the pipeline's existence read off the owning hub → the Update validators handed a typed
/// snapshot → the canonical write. A seam test cannot show that the stored shape actually reaches
/// the seam unchanged (the store or the read could re-type it first), nor that the update then
/// lands; this one asserts both.</para>
///
/// <para>🚨 <b>The negative control is load-bearing.</b> "No Error line" is vacuous unless the
/// recorder demonstrably SEES Error lines from that category. Content that names
/// <c>MarkdownContent</c> as its own <c>$type</c> and still will not bind is real corruption and
/// stays at Error. The control stores exactly that (<c>body</c>, a member the record does not
/// declare; a numeric <c>content</c> is refused at create by the content-schema validator) and
/// requires the line, through the same mesh,
/// the same recorder and the same update. The positive cases additionally require the pipeline's
/// Warning, so a run in which the seam was never reached cannot pass either.</para>
///
/// <para><b>No mocking.</b> A real monolith mesh, the real <c>Markdown</c> NodeType, the real
/// pipeline. The validator only records what it was handed and always answers Valid; the logger
/// provider only records.</para>
/// </summary>
public class UpdatingUndiscriminatedMarkdownIsNotAFaultTest : MonolithMeshTestBase
{
    private const string PipelineCategory = "MeshWeaver.Hosting.NodeUpdatePipeline";
    private const string StoredText = "# Stored body that must never reach a log";

    private readonly LineRecorder lines = new();

    public UpdatingUndiscriminatedMarkdownIsNotAFaultTest(ITestOutputHelper output) : base(output)
        // After the base constructor's ClearProviders().
        => Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(lines));

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services
            .AddSingleton<INodeValidator, ExistingContentRecorder>());

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private ExistingContentRecorder Validator =>
        Mesh.ServiceProvider.GetServices<INodeValidator>().OfType<ExistingContentRecorder>().Single();

    /// <summary>
    /// The two discriminator-less shapes the issue and its first fix measured: the legacy
    /// <c>{"markdown": …}</c> key, and a bare JSON string (a sanctioned markdown shape).
    /// Through the mesh they take DIFFERENT branches: the legacy key arrives as an untyped
    /// <see cref="JsonElement"/> and reaches the admitted-not-claimed split, while the bare string
    /// is read back as a CLR <see cref="string"/> and is refused earlier by
    /// <c>ContentDiscriminator.Admits</c>. Each case pins the shape it saw, so a change to either
    /// branch shows up here by name.
    /// </summary>
    [Theory(Timeout = 180000)]
    [InlineData("""{"markdown":"# Stored body that must never reach a log"}""", typeof(JsonElement), "a JSON object")]
    [InlineData("\"# Stored body that must never reach a log\"", typeof(string), "System.String")]
    public async Task UpdatingMarkdownStoredWithoutDiscriminator_Lands_AndFilesNoError(
        string storedJson, Type storedAs, string shape)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await CreateWithStoredContent(storedJson);

        var updated = await MeshService.UpdateNode(Edited(path)).Take(1)
            .Timeout(60.Seconds()).Await(ct);
        updated.Path.Should().Be(path);

        var seen = await Validator.Seen(path).Timeout(60.Seconds()).Await(ct);
        seen.Should().Be(storedAs,
            "the stored shape must reach the update seam as stored — never re-typed into a "
            + "MarkdownContent, which would make this test say nothing about #5736 (the legacy key "
            + "stays an untyped JsonElement; a bare JSON string is read back as a CLR string)");

        var after = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n?.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)?.Content == "# Edited")
            .FirstAsync().Timeout(60.Seconds()).Await(ct);
        after.Should().NotBeNull("the update that replaces the legacy content must land");

        var forPath = lines.For(PipelineCategory, path);
        foreach (var e in forPath)
            Output.WriteLine($"{e.Level}: {e.Text}");
        forPath.Should().NotContain(e => e.Level >= LogLevel.Error,
            "content that never claimed to be a MarkdownContent failing to bind as one is an "
            + "answer, not a failed recovery (#5736)");
        forPath.Should().Contain(e => e.Level == LogLevel.Warning && e.Text.Contains(shape),
            "the seam was reached and said, once, that the validators saw an untyped snapshot");
        forPath.Should().NotContain(e => e.Text.Contains(StoredText),
            "the pipeline names the path, the type and the shape — never the content");
    }

    /// <summary>
    /// NEGATIVE CONTROL: stored content that NAMES <c>MarkdownContent</c> and does not bind is
    /// corruption, and the same mesh, update and recorder MUST surface it at Error. If this ever
    /// passes with no line, the recorder is blind and the positive case above proves nothing.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task UpdatingMarkdownThatClaimsItsTypeAndDoesNotBind_IsStillAnError()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await CreateWithStoredContent("""{"$type":"MarkdownContent","body":"# Stored body that must never reach a log"}""");

        await MeshService.UpdateNode(Edited(path)).Take(1).Timeout(60.Seconds()).Await(ct);
        await Validator.Seen(path).Timeout(60.Seconds()).Await(ct);

        var forPath = lines.For(PipelineCategory, path);
        foreach (var e in forPath)
            Output.WriteLine($"{e.Level}: {e.Text}");
        forPath.Should().Contain(e => e.Level == LogLevel.Error && e.Text.Contains("could not recover value"),
            "a payload that CLAIMS to be a MarkdownContent and is not one stays an Error");
    }

    private async Task<string> CreateWithStoredContent(string storedJson)
    {
        var id = "md" + Guid.NewGuid().ToString("N")[..8];
        var path = $"{TestPartition}/{id}";
        await MeshService.CreateNode(new MeshNode(id, TestPartition)
            {
                Name = "Legacy markdown",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
                Content = JsonDocument.Parse(storedJson).RootElement.Clone(),
            })
            .Take(1)
            .Should().Within(60.Seconds())
            .Emit("the legacy-shaped node must exist first", cancellationToken: TestContext.Current.CancellationToken);
        return path;
    }

    private static MeshNode Edited(string path) => MeshNode.FromPath(path) with
    {
        Name = "Legacy markdown",
        NodeType = "Markdown",
        State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = "# Edited" },
    };

    /// <summary>Records the CLR type of <c>ExistingNode.Content</c> per path; always Valid.</summary>
    private sealed class ExistingContentRecorder : INodeValidator
    {
        private readonly ReplaySubject<(string Path, Type? Type)> seen = new();

        public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Update];

        public IObservable<Type?> Seen(string path)
            => seen.Where(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Type)
                .FirstAsync();

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        {
            if (context.ExistingNode is { } existing)
                seen.OnNext((existing.Path, existing.Content?.GetType()));
            return Observable.Return(NodeValidationResult.Valid());
        }
    }

    /// <summary>Records every line at Warning or above, with its category. Instance state.</summary>
    private sealed class LineRecorder : ILoggerProvider
    {
        private ImmutableList<(string Category, LogLevel Level, string Text)> entries =
            ImmutableList<(string, LogLevel, string)>.Empty;

        public ImmutableList<(string Category, LogLevel Level, string Text)> All => entries;

        public ImmutableList<(string Category, LogLevel Level, string Text)> For(string category, string path)
            => entries.Where(e => e.Category == category && e.Text.Contains(path, StringComparison.Ordinal))
                .ToImmutableList();

        public ILogger CreateLogger(string categoryName) => new Recording(this, categoryName);

        public void Dispose() { }

        private void Add(string category, LogLevel level, string text)
            => ImmutableInterlocked.Update(ref entries, list => list.Add((category, level, text)));

        private sealed class Recording(LineRecorder owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                    owner.Add(category, logLevel, formatter(state, exception));
            }
        }
    }
}
