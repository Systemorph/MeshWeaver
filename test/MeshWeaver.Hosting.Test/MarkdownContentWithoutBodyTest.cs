using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Systemorph/MeshWeaver#6639. A <c>Markdown</c> row whose stored content names
/// <c>$type: MarkdownContent</c> but carries no <c>content</c> member does not bind — and that is
/// deliberate: <c>MarkdownContent.Content</c> is <c>required</c> precisely so that content carrying
/// its body under another key (<c>{"markdown": …}</c>, <c>{"text": …}</c>) THROWS instead of
/// binding into an empty page (#4600, #5736). What was wrong is the diagnosis: the warning that
/// names the node's PATH said "TypeRegistry lacks the $type discriminator" for a type the process
/// knows, sending the reader to fix a registration that is not missing, while the serializer's own
/// warning — which does name the cause — cannot name the node.
///
/// <para>All arms drive the production read seams (<c>MeshNodeStreamCache.ConvertContentJsonElementToTyped</c>
/// and <c>DeserializeContent</c>) with a real <see cref="MeshContentTypeRegistry"/> — no mocking.</para>
/// </summary>
public class MarkdownContentWithoutBodyTest
{
    private const string MarkdownNodeType = "Markdown";

    /// <summary>The two read seams that can degrade content.</summary>
    public static TheoryData<string> Seams() => ["GetStream", "GetQuery"];

    private static MeshNode Run(string seam, MeshNode node, ILogger logger, IMeshContentTypeRegistry registry)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return seam switch
        {
            "GetStream" => MeshNodeStreamCache.ConvertContentJsonElementToTyped(node, options, logger, registry),
            "GetQuery" => MeshNodeStreamCache.DeserializeContent(node, options, logger, registry),
            _ => throw new ArgumentOutOfRangeException(nameof(seam), seam, "unknown read seam"),
        };
    }

    private static MeshNode NodeWith(string nodeType, string contentJson) =>
        new("APageWithoutABody", "Space")
        {
            NodeType = nodeType,
            Content = JsonSerializer.Deserialize<JsonElement>(contentJson),
        };

    /// <summary>
    /// The measured shape: <c>$type: MarkdownContent</c>, body under a key the record does not
    /// declare. It stays raw (the contract), and the path-bearing warning says the TYPE IS KNOWN
    /// and the bytes do not bind. Negative control: on main both seams printed the registration
    /// text for this node.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seams))]
    public void MarkdownContentWithoutABody_IsReportedAsABindFailure_WithItsPath(string seam)
    {
        var registry = new MeshContentTypeRegistry();
        registry.Register(typeof(MarkdownContent), MarkdownNodeType);
        var logger = new RecordingLogger();

        var read = Run(seam,
            NodeWith(MarkdownNodeType, """{"$type":"MarkdownContent","text":"the body, under the wrong key"}"""),
            logger, registry);

        Assert.IsType<JsonElement>(read.Content);
        var record = Assert.Single(logger.Records.Where(r => r.Level >= LogLevel.Warning));
        Assert.Contains("Space/APageWithoutABody", record.Message);
        Assert.Contains("'MarkdownContent' is registered, but the stored JSON does not bind", record.Message);
        Assert.DoesNotContain("TypeRegistry lacks", record.Message);
    }

    /// <summary>A record with a genuinely <c>required</c> member — content that omits it cannot bind.</summary>
    public record RequiredBodyContent
    {
        /// <summary>The member a partial write omits.</summary>
        public required string Body { get; init; }
    }

    /// <summary>
    /// The same for any registered type with a <c>required</c> member. The type IS registered, so the path-bearing warning must name the bind
    /// failure, not "TypeRegistry lacks the $type discriminator" — which sends the reader to fix a
    /// registration that is not missing. Negative control: on main both seams printed the
    /// registration text for this node.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seams))]
    public void UnbindableContentOfAKnownType_IsReportedAsABindFailure_WithItsPath(string seam)
    {
        const string nodeType = "Probe/RequiredBody";
        var registry = new MeshContentTypeRegistry();
        registry.Register(typeof(RequiredBodyContent), nodeType);
        var logger = new RecordingLogger();

        var read = Run(seam, NodeWith(nodeType, """{"$type":"RequiredBodyContent","label":"no body"}"""),
            logger, registry);

        Assert.IsType<JsonElement>(read.Content);
        var record = Assert.Single(logger.Records.Where(r => r.Level >= LogLevel.Warning));
        Assert.Contains("Space/APageWithoutABody", record.Message);
        Assert.Contains("'RequiredBodyContent' is registered, but the stored JSON does not bind", record.Message);
        Assert.DoesNotContain("TypeRegistry lacks", record.Message);
    }

    /// <summary>
    /// The other cause keeps its own words: a <c>$type</c> nothing in the process resolves is still
    /// reported as a missing registration.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seams))]
    public void UnresolvableType_IsStillReportedAsAMissingRegistration(string seam)
    {
        var logger = new RecordingLogger();

        Run(seam, NodeWith("Probe/Unknown", """{"$type":"NothingKnowsThis","label":"x"}"""),
            logger, new MeshContentTypeRegistry());

        var record = Assert.Single(logger.Records.Where(r => r.Level >= LogLevel.Warning));
        Assert.Contains("TypeRegistry lacks the $type discriminator", record.Message);
    }

    private sealed record Record(LogLevel Level, string Message);

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<Record> records = [];

        public IReadOnlyList<Record> Records => records;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => records.Add(new Record(logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
