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
/// Systemorph/MeshWeaver#6639. A <c>Markdown</c> row whose stored content carries no
/// <c>content</c> member — a page written with only tags or a thumbnail, or a partial write the
/// content-schema guard admits by design — must read as a TYPED <see cref="MarkdownContent"/> with
/// an empty body, not degrade to raw JSON. And when content of a KNOWN type does fail to bind, the
/// warning that names the node's PATH must say so, rather than blaming a registration that is not
/// missing.
///
/// <para>Both arms drive the production read seams (<c>MeshNodeStreamCache.ConvertContentJsonElementToTyped</c>
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
    /// The contract half. Negative control: with <c>MarkdownContent.Content</c> declared
    /// <c>required</c> (main before this change) the bind throws "missing required properties
    /// including: 'content'", the registry's recovery answers null, and the node leaves the seam as
    /// a raw <see cref="JsonElement"/> — the tags it carries invisible to every typed reader.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seams))]
    public void MarkdownContentWithoutABody_ReadsTyped_WithAnEmptyBody(string seam)
    {
        var registry = new MeshContentTypeRegistry();
        registry.Register(typeof(MarkdownContent), MarkdownNodeType);
        var logger = new RecordingLogger();

        var read = Run(seam,
            NodeWith(MarkdownNodeType, """{"$type":"MarkdownContent","tags":["kept"],"abstract":"a summary"}"""),
            logger, registry);

        var typed = Assert.IsType<MarkdownContent>(read.Content);
        Assert.Equal(string.Empty, typed.Content);
        Assert.Equal(["kept"], typed.Tags);
        Assert.Equal("a summary", typed.Abstract);
        Assert.Empty(logger.Records.Where(r => r.Level >= LogLevel.Warning));
    }

    /// <summary>A record with a genuinely <c>required</c> member — content that omits it cannot bind.</summary>
    public record RequiredBodyContent
    {
        /// <summary>The member a partial write omits.</summary>
        public required string Body { get; init; }
    }

    /// <summary>
    /// The log half. The type IS registered, so the path-bearing warning must name the bind
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
