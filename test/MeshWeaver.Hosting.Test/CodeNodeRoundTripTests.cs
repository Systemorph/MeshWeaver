using System;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the #912 fix: a <see cref="CodeConfiguration"/> that carries state a bare <c>.cs</c> file
/// cannot represent (IsExecutable, ActivityParentPath, Last* execution stamps, a non-C# language,
/// unknown members) must NOT be claimed by <see cref="CSharpFileParser"/> — it falls through to
/// the lossless whole-node JSON form. Before the fix the parser claimed every CodeConfiguration
/// and wrote only the code, so on every FS/blob/GitSync round-trip an executable Code node came
/// back non-executable and ExecuteScript refused it. Pure C# source keeps its <c>.cs</c> form —
/// that file-format contract (GitSync writes readable source files into users' repos) is pinned
/// by <c>TypedNodeExportRobustnessTest.CodeSourceUnderSourceSegment_ExportsAsCsAndRoundTrips</c>.
/// </summary>
public class CodeNodeRoundTripTests : IDisposable
{
    private static readonly JsonSerializerOptions Options = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("mw-code-roundtrip-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static MeshNode CodeNode(string id, CodeConfiguration config) =>
        new(id, "Scripts") { Name = id, NodeType = "Code", Content = config };

    // ---- serializer selection (the actual #912 gate) --------------------------------

    [Fact]
    public void PureSource_IsClaimedByCSharpParser()
    {
        var parser = new CSharpFileParser();
        Assert.True(parser.CanSerialize(CodeNode("a", new CodeConfiguration { Code = "1+1" })));
    }

    [Theory]
    [InlineData("executable")]
    [InlineData("activityParent")]
    [InlineData("lastExecutedAt")]
    [InlineData("lastExecutedBy")]
    [InlineData("lastExecutedCodeHash")]
    [InlineData("lastActivityPath")]
    [InlineData("language")]
    public void ConfigWithNonRepresentableState_IsDeclined(string field)
    {
        var config = field switch
        {
            "executable" => new CodeConfiguration { Code = "1+1", IsExecutable = true },
            "activityParent" => new CodeConfiguration { Code = "1+1", ActivityParentPath = "me" },
            "lastExecutedAt" => new CodeConfiguration { Code = "1+1", LastExecutedAt = DateTimeOffset.UnixEpoch },
            "lastExecutedBy" => new CodeConfiguration { Code = "1+1", LastExecutedBy = "user" },
            "lastExecutedCodeHash" => new CodeConfiguration { Code = "1+1", LastExecutedCodeHash = "abc" },
            "lastActivityPath" => new CodeConfiguration { Code = "1+1", LastActivityPath = "me/_Activity/1" },
            "language" => new CodeConfiguration { Code = "print(1)", Language = "python" },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var parser = new CSharpFileParser();
        var node = CodeNode("a", config);

        Assert.False(parser.CanSerialize(node));
        // Fail-loud guard: serializing such a config as .cs would silently strip the state.
        Assert.Throws<InvalidOperationException>(() => parser.Serialize(node));
    }

    // ---- file-system round-trip ------------------------------------------------------

    [Fact]
    public async Task ExecutableCodeNode_RoundTripsLosslessly_AsJson()
    {
        var adapter = new FileSystemStorageAdapter(_dir);
        var node = CodeNode("exec", new CodeConfiguration
        {
            Code = "Console.WriteLine(\"hi\"); 1+1",
            IsExecutable = true,
            ActivityParentPath = "me",
            LastExecutedBy = "user",
        });

        await adapter.Write(node, Options).FirstAsync();

        Assert.True(File.Exists(Path.Combine(_dir, "Scripts", "exec.json")),
            "a config a .cs file cannot represent must persist as whole-node JSON");
        Assert.False(File.Exists(Path.Combine(_dir, "Scripts", "exec.cs")));

        var read = await adapter.Read("Scripts/exec", Options).FirstAsync();
        var config = read.ContentAs<CodeConfiguration>(Options)!;
        Assert.True(config.IsExecutable);
        Assert.Equal("me", config.ActivityParentPath);
        Assert.Equal("user", config.LastExecutedBy);
        Assert.Equal(node.ContentAs<CodeConfiguration>(Options)!.Code, config.Code);
    }

    [Fact]
    public async Task PureSourceCodeNode_KeepsItsCsFileForm()
    {
        var adapter = new FileSystemStorageAdapter(_dir);
        var node = CodeNode("model", new CodeConfiguration { Code = "public record Person(string Name);" });

        await adapter.Write(node, Options).FirstAsync();

        var csPath = Path.Combine(_dir, "Scripts", "model.cs");
        Assert.True(File.Exists(csPath), "pure C# source must stay a readable .cs source file");
        Assert.Equal("public record Person(string Name);", await File.ReadAllTextAsync(csPath));

        var read = await adapter.Read("Scripts/model", Options).FirstAsync();
        Assert.Equal("public record Person(string Name);", read.ContentAs<CodeConfiguration>(Options)!.Code);
    }

    [Fact]
    public async Task MarkingAPersistedSourceNodeExecutable_MigratesCsToJson()
    {
        // The upgrade path for data written by pre-fix builds: the node exists on disk as .cs;
        // the next write (now carrying executable state) must flip it to .json AND remove the
        // stale .cs — the read side prefers .cs over .json, so a leftover .cs would shadow the
        // new file with the metadata-stripped version forever.
        var adapter = new FileSystemStorageAdapter(_dir);
        await adapter.Write(CodeNode("cell", new CodeConfiguration { Code = "1+1" }), Options).FirstAsync();
        Assert.True(File.Exists(Path.Combine(_dir, "Scripts", "cell.cs")));

        await adapter.Write(
            CodeNode("cell", new CodeConfiguration { Code = "1+1", IsExecutable = true }), Options).FirstAsync();

        Assert.False(File.Exists(Path.Combine(_dir, "Scripts", "cell.cs")),
            "the stale .cs must be cleaned up or it shadows the .json on read");
        var read = await adapter.Read("Scripts/cell", Options).FirstAsync();
        Assert.True(read.ContentAs<CodeConfiguration>(Options)!.IsExecutable);
    }
}
