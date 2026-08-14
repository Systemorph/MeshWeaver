using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// A synced source repo is full of ordinary JSON that is not a node — <c>package.json</c>,
/// <c>tsconfig.json</c>, <c>launchSettings.json</c>, lock files.
///
/// <para><b>Measured on memex-cloud, 2026-08-14 10:14:14Z</b> (pod
/// <c>memex-portal-deployment-79867669f4-f2zzl</c>): every sync of this very repository logged
/// <c>Failed to parse MeshWeaver/clients/grpc-web/package.json — file skipped on import</c>, with
/// <c>The JSON value could not be converted to System.Int64. Path: $.version</c> — npm's
/// <c>"version": "0.1.0"</c> is a string where <see cref="MeshNode.Version"/> is numeric. That
/// throw is reported as an Error on the import activity, and <c>ActivityRunner.Finish</c> rolls it
/// up, so the import terminates <c>Failed</c> — permanently, on every run, for a file that was
/// never a node.</para>
///
/// <para>The quieter half is worse: a document with no overlapping keys deserializes
/// "successfully" into an all-default <see cref="MeshNode"/>, so the alternative to a red import
/// was a silent empty node. Both are the same missing decision — whether the file is a node at
/// all — which is now made structurally, before deserializing.</para>
/// </summary>
public class OrdinaryJsonIsNotANodeFileTest
{
    private static readonly JsonFileParser Parser = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    });

    private static MeshNode? Parse(string content) => Parser.Parse("f.json", content, "Space/f.json");

    /// <summary>The exact file, and the exact failure, from the production log.</summary>
    [Fact]
    public void TheNpmManifestThatFailedEveryImportIsSimplyNotANode()
    {
        const string packageJson = """
            {
              "name": "@meshweaver/grpc-web",
              "version": "0.1.0",
              "description": "gRPC-web transport for MeshWeaver clients",
              "main": "dist/index.js",
              "scripts": { "build": "tsc" }
            }
            """;

        Parse(packageJson).Should().BeNull(
            "an npm manifest is not a node — reporting it as a failed parse turned every sync of a "
            + "repo containing one into a permanently red import");
    }

    /// <summary>
    /// The quiet half: no overlapping keys, so it USED to deserialize into an all-default node.
    /// Silence is not the right answer either — absence is.
    /// </summary>
    [Fact]
    public void AConfigFileDoesNotBecomeAnEmptyNode()
    {
        const string tsconfig = """
            { "compilerOptions": { "target": "ES2022", "strict": true }, "include": ["src"] }
            """;

        Parse(tsconfig).Should().BeNull(
            "deserializing this into a MeshNode succeeds and yields an all-default node — a silent "
            + "empty node in the mesh is worse than the loud failure, not better");
    }

    /// <summary>
    /// 🚨 The regression guard that matters: every authored node file must still parse. The marker
    /// set was chosen against the whole corpus (597/597 authored node .json files carry one of
    /// these three), so these cases are representative rather than illustrative.
    /// </summary>
    [Theory]
    [InlineData("""{"id":"SampleStatistics","namespace":"Doc/Architecture","name":"Sample statistics"}""")]
    [InlineData("""{"$type":"MeshNode","id":"Doc","path":"Doc","name":"Doc","nodeType":"Space","version":1}""")]
    [InlineData("""{"nodeType":"Code","name":"A node whose id comes from its path"}""")]
    [InlineData("""{"Id":"PascalCased","Name":"Hand-authored"}""")]
    public void AnAuthoredNodeFileStillParses(string content)
    {
        Parse(content).Should().NotBeNull(
            "the change may only ever REMOVE files from the node set; a node that parsed before "
            + "must parse now, or the fix costs content");
    }

    /// <summary>
    /// Malformed JSON still THROWS, because that genuinely is a broken node file and the import
    /// activity is supposed to name it. Returning null here would trade a reported failure for a
    /// silently missing node — the defect the throw was introduced to fix.
    /// </summary>
    [Fact]
    public void MalformedJsonStillThrowsSoTheImportCanReportIt()
    {
        Action act = () => _ = Parse("""{"id":"Broken", "name": }""");

        act.Should().Throw<JsonException>(
            "a file that cannot be parsed at all is not the same as a file that is not a node");
    }

    /// <summary>
    /// A JSON array or scalar is a document, never a node — and must not throw on the way to
    /// saying so.
    /// </summary>
    [Theory]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    public void ANonObjectDocumentIsNotANode(string content) =>
        Parse(content).Should().BeNull();
}
