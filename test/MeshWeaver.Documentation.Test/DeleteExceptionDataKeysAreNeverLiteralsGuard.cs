using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>#7348 — the recursive-delete pipeline's <see cref="System.Exception.Data"/> keys are spelled
/// through their constants, never as bare literals.</b>
///
/// <para>A failed recursive delete hands the paths it already removed to its caller under
/// <c>HierarchicalPathDeletion.DeletedPathsDataKey</c>, and the stage / unanswered-paths keys beside
/// it. Two of the four writers of the deleted-paths key spelled it as a bare string while the reader
/// went through the constant. Every spelling agreed, so nothing failed — but renaming the constant
/// would have left those two writers on the old key, and the partial-delete count would have
/// under-reported with no error and nothing to grep. An inventory taken by grepping the constant
/// already read short once and put a wrong count into a code comment.</para>
///
/// <para>The guard reads source text for an indexer whose argument is one of the three key VALUES
/// as a string literal. A declaration (<c>const string … = "DeletedPaths"</c>) is not an indexer
/// and does not match.</para>
/// </summary>
public class DeleteExceptionDataKeysAreNeverLiteralsGuard
{
    private static readonly Regex LiteralKeyIndexer = new(
        @"\.Data\s*\[\s*""(DeletedPaths|DeleteStage|DeleteUnansweredPaths)""\s*\]",
        RegexOptions.Compiled);

    internal static string[] Offenders(string root) =>
        SourceScan.SourceFiles(root, ["src", "test"])
            .Where(f => LiteralKeyIndexer.IsMatch(File.ReadAllText(f)))
            .Select(f => SourceScan.Relative(root, f))
            .ToArray();

    [Fact]
    public void NoSourceIndexesExceptionDataWithALiteralDeleteKey()
    {
        var offenders = Offenders(RepoPaths.Root);
        Assert.True(offenders.Length == 0,
            "These files index Exception.Data with a bare recursive-delete key literal; use "
            + "HierarchicalPathDeletion.DeletedPathsDataKey (or the stage / unanswered constants) "
            + "so a rename cannot split the key (#7348):\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>The detector sees the shape it forbids, and ignores the constant's declaration.</summary>
    [Fact]
    public void TheDetector_SeesAPlantedLiteral_AndNotTheDeclaration()
    {
        var dir = Directory.CreateTempSubdirectory("delete-key-guard");
        try
        {
            var src = Directory.CreateDirectory(Path.Combine(dir.FullName, "src", "X"));
            File.WriteAllText(Path.Combine(src.FullName, "Literal.cs"),
                "class L { void M(System.Exception ex) { ex.Data[\"DeletedPaths\"] = null; } }\n");
            File.WriteAllText(Path.Combine(src.FullName, "Declared.cs"),
                "class D { public const string DeletedPathsDataKey = \"DeletedPaths\";\n"
                + "  void M(System.Exception ex) { ex.Data[DeletedPathsDataKey] = null; } }\n");

            Assert.Equal(["src/X/Literal.cs"], Offenders(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
