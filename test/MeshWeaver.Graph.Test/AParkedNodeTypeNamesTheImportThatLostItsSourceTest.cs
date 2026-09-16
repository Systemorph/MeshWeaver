using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>Issue #4469 — a NodeType parked on a source node the import could not write must NAME the
/// import, not just <c>CS0246</c>.</b>
///
/// <para><b>Measured on memex.systemorph.com, 2026-09-15.</b> One file —
/// <c>Hosting/Deployment/Source/SelfUpdateRouting.cs</c> — carried a literal NUL byte, which
/// PostgreSQL cannot store in a text column, so the import refused that ONE row while forty others
/// landed. The files that REFERENCE the symbol did land, so the partition was left
/// referenced-but-incomplete and five Hosting NodeTypes parked on</para>
/// <code>
/// CS0246: The type or namespace name 'SelfUpdateRouting' could not be found
/// </code>
/// <para>— on a symbol whose file is plainly in git. <c>Hosting/InstanceAction</c> was one of the
/// five, so NO instance action ran on the control instance at all: no Sample, no Logs, no
/// HelmRelease, no Roll. Hours went into reaching a conclusion the system already had every fact
/// to state. #4467 fixed the import half (the refusal is remembered per node, with its reason, and
/// the sync activity names the file). This is the half it deliberately did not reach: the operator
/// who starts from the COMPILE ERROR.</para>
///
/// <para>🚨 <b>The hard part is the DENOMINATOR, and the control arm below is the whole test.</b> A
/// symbol can be unresolved because an import lost the file, because it was legitimately DELETED,
/// or because the module carrying it is not loaded on this replica (a declined bundle,
/// MeshWeaver#3583 — a different defect with a different fix). Asserting an import refusal where
/// none is established is WORSE than the current silence. And refusing to compile — or accusing —
/// a whole partition because ONE unrelated node was refused would re-create exactly the over-broad
/// granularity #4467 had just removed from the import, one layer up. So the second case compiles a
/// SECOND type in the SAME partition, under the SAME recorded refusal, failing on a DIFFERENT
/// unresolved name, and requires it to be told nothing at all.</para>
/// </summary>
public class AParkedNodeTypeNamesTheImportThatLostItsSourceTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    // 🚨 A DERIVED FIELD INITIALIZER RUNS BEFORE THE BASE CONSTRUCTOR, and the base constructor is
    // what calls ConfigureMesh — so both of these are set when the validator is registered.
    private readonly string _partition = "Ir" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>The one path the test validator refuses, standing in for the NUL byte. A validator
    /// refusal reaches the importer as <c>NodeUpsertRejectionReason.ValidationFailed</c>, which
    /// <c>StaticRepoImporter.IsContentVerdict</c> classifies as a verdict about the BYTES — the
    /// same classification the live Postgres refusal earned, without needing a store that refuses a
    /// byte.</summary>
    private string? _refusePath;

    /// <summary>The symbol the lost file would have defined — the test's <c>SelfUpdateRouting</c>.
    /// The node is named for it, exactly as a C# source node is.</summary>
    private const string LostSymbol = "Ir4469Routing";

    /// <summary>A symbol NOTHING in this mesh ever declares — the control arm's unresolved name.
    /// No import refusal names it, so no import verdict may be attached to a failure on it.</summary>
    private const string NeverDeclaredSymbol = "Ir4469NobodyDeclaresThis";

    /// <summary>The reason the refusing validator gives. Asserted verbatim on the operator-visible
    /// text: <i>"refused"</i> alone cannot separate a byte the store will not take from a validator
    /// rule from an RLS denial, and those have three different fixes (#3101's argument).</summary>
    private string RefusalReason => $"'{_refusePath}' is refused by the test validator";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<INodeValidator>(
                new RefuseOnePathValidator(() => _refusePath)));

    /// <summary>
    /// 🚨 <b>THE PIN FOR #4469.</b> An import loses one shared source node; a NodeType that
    /// REFERENCES its symbol — and does not own it, exactly as four of the five Hosting types did
    /// not — then fails to compile. The operator-visible record on the parked type must say that an
    /// import refused that specific file, and why.
    ///
    /// <para>Pre-fix, everything below the first assertion is the only thing that exists:
    /// <c>CS0246 'Ir4469Routing' could not be found</c>, on a file the source tree plainly
    /// declares.</para>
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task ANodeTypeParkedOnARefusedSource_NamesTheImportAndTheReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var referencing = $"{_partition}/Referencing";

        await ImportTheLibraryWithOneRefusedFile(ct);

        // Only NOW is the NodeType created — after the import has completed and written its
        // manifest. Deliberate: a type created inside the import would race the bookkeeping the
        // diagnosis reads, and a test that sometimes measures a compile from before the ledger
        // landed measures nothing on those runs.
        var def = await CompileAndSettle(
            referencing,
            $$"""
              /// <summary>Content of the type that references the lost file's symbol.</summary>
              public record Ir4469ReferencingContent
              {
                  /// <summary>Binds the symbol whose file the import could not write.</summary>
                  public int Answer { get; init; } = {{LostSymbol}}.Answer();
              }
              """,
            ct);

        def.CompilationStatus.Should().Be(CompilationStatus.Error,
            "the symbol's file is not in the mesh, so this type genuinely cannot compile — the "
            + $"recorded error was: {def.CompilationError}");
        def.CompilationDiagnostics.Should()
            .Contain(d => ImportRefusalDiagnosis.UnresolvedNameIds.Contains(d.Id),
                "the failure must be the unresolved-NAME shape the diagnosis is scoped to — and it "
                + "is asserted against the SET the join actually reads, never one literal: the same "
                + "missing file earns CS0103 in expression position and CS0246 in type position, so "
                + "a test pinned to one of them would go green about a join that never fired");

        // 🚨 THE OPERATOR-VISIBLE TEXT. This is what get_diagnostics returns, what the Settings →
        // Progress error page renders, and what `search content.compilationStatus:Error` finds.
        def.CompilationError.Should().NotBeNull();
        def.CompilationError!.Should().Contain("REFUSED BY AN IMPORT",
            "an operator who lands on the compile error must be told the cause is an IMPORT, not "
            + "the code in front of them — that sentence is the whole issue");
        def.CompilationError.Should().Contain(_refusePath!,
            "and WHICH file, because the repair is in the repository: five Hosting NodeTypes named "
            + "a symbol and not one of them named the node that never landed");
        def.CompilationError.Should().Contain(RefusalReason,
            "and WHY — 'refused' alone cannot separate a byte Postgres will not store from a "
            + "validator rule from an RLS denial, and those have three different fixes");

        // 🚨 THE STRUCTURED HALF, under the three-answers rule: non-empty means ESTABLISHED.
        def.CompilationImportRefusals.Should().NotBeNull(
            "null is the NOT-DETERMINED value and must never stand for a refusal that was read");
        def.CompilationImportRefusals!.Select(r => r.NodePath).Should().Contain(_refusePath!);
        def.CompilationImportRefusals!.Single(r => r.NodePath == _refusePath).Reason
            .Should().Contain("refused by the test validator",
                "the reason travels with the refusal in the import manifest — a path without one "
                + "sends the reader back to the import activity to find out what happened");
    }

    /// <summary>
    /// 🚨 <b>THE CASE THAT COULD FALSIFY THE ONE ABOVE.</b> Same partition, same standing refusal
    /// in the same manifest, a compile that also fails on an unresolved name — but a DIFFERENT one,
    /// that no refused file would have defined.
    ///
    /// <para>Without this, a diagnosis that simply announced "this partition has a refusal" on
    /// every failed compile in the partition would pass the test above, and it would be the same
    /// over-broad granularity #4459 was about: one refused node speaking for forty. It would also
    /// be an accusation from evidence nobody has — a symbol is equally missing when it was
    /// deliberately deleted, or when the module carrying it is not loaded on this replica
    /// (MeshWeaver#3583).</para>
    ///
    /// <para>EMPTY, not null, is the required answer: the bookkeeping WAS read and explains
    /// nothing. "I could not look" and "I looked and found nothing" must not collapse.</para>
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task ANodeTypeFailingOnADifferentSymbol_IsNotAccusedOfTheSamePartitionsRefusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var unrelated = $"{_partition}/Unrelated";

        await ImportTheLibraryWithOneRefusedFile(ct);

        var def = await CompileAndSettle(
            unrelated,
            $$"""
              /// <summary>Content of the control type, which references nothing that was refused.</summary>
              public record Ir4469UnrelatedContent
              {
                  /// <summary>Binds a symbol nothing anywhere declares.</summary>
                  public int Answer { get; init; } = {{NeverDeclaredSymbol}}.Answer();
              }
              """,
            ct);

        def.CompilationStatus.Should().Be(CompilationStatus.Error,
            $"this type cannot compile either — the recorded error was: {def.CompilationError}");
        def.CompilationDiagnostics.Should()
            .Contain(d => ImportRefusalDiagnosis.UnresolvedNameIds.Contains(d.Id),
                "🚨 the control must fail the SAME WAY as the case above — an unresolved NAME. A "
                + "control that failed for some other reason would never have reached the join at "
                + "all, and would prove nothing about its scoping");

        def.CompilationError!.Should().NotContain("REFUSED BY AN IMPORT",
            "no refused file would have declared this symbol. Saying an import dropped it — on the "
            + "evidence that SOMETHING in the partition was refused — is exactly the over-broad "
            + "reading #4459 was about, and it is worse than silence: it sends the operator to the "
            + "repository for a symbol that was never there");
        def.CompilationError.Should().Contain(NeverDeclaredSymbol,
            "while the genuine diagnostics are of course still reported in full");

        def.CompilationImportRefusals.Should().NotBeNull(
            "the bookkeeping WAS read: this compile failed on an unresolved name, so the question "
            + "was put. Null here would mean 'could not look', which is a different fact");
        def.CompilationImportRefusals!.Should().BeEmpty(
            "read, and it explains nothing about THESE names — which is what sends the reader to "
            + "the other two causes rather than to the repository");
    }

    // ---- fixture ------------------------------------------------------------------------------

    /// <summary>
    /// The import that loses one file: a shared source folder whose <c>{LostSymbol}</c> node the
    /// validator refuses while its sibling lands. Deliberately carries NO NodeType — a type created
    /// by the import would begin compiling while the import is still running, against a source set
    /// and a manifest that are both still moving.
    /// </summary>
    private async Task ImportTheLibraryWithOneRefusedFile(CancellationToken ct)
    {
        _refusePath = $"{_partition}/Lib/{CodeConventions.SourceSubNamespace}/{LostSymbol}";
        var source = new FixtureSource(_partition)
        {
            Root = Space(_partition),
            Nodes =
            [
                Code(_partition, $"Lib/{CodeConventions.SourceSubNamespace}", LostSymbol,
                    $$"""
                      /// <summary>The file the import cannot write.</summary>
                      public static class {{LostSymbol}}
                      {
                          /// <summary>The answer.</summary>
                          public static int Answer() => 7;
                      }
                      """),
                Code(_partition, $"Lib/{CodeConventions.SourceSubNamespace}", "Ir4469Sibling",
                    """
                    /// <summary>A file that lands, so the refusal is per NODE and not per folder.</summary>
                    public static class Ir4469Sibling
                    {
                        /// <summary>One.</summary>
                        public static int One() => 1;
                    }
                    """),
            ],
        };

        // 🚨 .Await(), never a bare `await` on the observable: Rx's own awaiter resumes the
        // continuation INLINE on the signalling thread, inside the trampoline.
        var result = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(240.Seconds()).Await(ct);
        Output.WriteLine(
            $"import outcome={result.Outcome} count={result.Count} failed={result.Failed} "
            + $"written=[{string.Join(", ", result.WrittenPaths)}]");

        result.FailedPaths.Select(f => f.NodePath).Should().Contain(_refusePath!,
            "the whole test rests on this ONE node having been refused and recorded — if the "
            + "import stopped refusing it, everything below would be measuring a partition that "
            + "lost nothing");
        result.WrittenPaths.Should().Contain(
            $"{_partition}/Lib/{CodeConventions.SourceSubNamespace}/Ir4469Sibling",
            "and on the refusal being PER NODE: a folder that failed wholesale would make the "
            + "compile below fail for a reason this test is not about");
    }

    /// <summary>
    /// Creates a NodeType and its own source, then waits for the real compile to settle and returns
    /// the definition as the mesh persisted it — the production write-back, not a re-derivation.
    /// The type draws on the refused library through a SHARED source query, which is the shape four
    /// of the five parked Hosting types had: they referenced <c>SelfUpdateRouting</c> and none of
    /// them owned it.
    /// </summary>
    private async Task<NodeTypeDefinition> CompileAndSettle(
        string nodeTypePath, string ownSource, CancellationToken ct)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var typeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = nodeTypePath[(nodeTypePath.LastIndexOf('/') + 1)..],
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Description = "references a symbol whose file the import could not write",
                Configuration = "config => config",
                Sources =
                [
                    $"namespace:{CodeConventions.SourceSubNamespace} scope:subtree",
                    $"shared=@{_partition}/Lib/{CodeConventions.SourceSubNamespace}",
                ],
            },
        };

        await meshService.CreateNode(typeNode)
            .SelectMany(_ => meshService.CreateNode(
                Code(nodeTypePath, CodeConventions.SourceSubNamespace, "Main", ownSource)))
            .FirstAsync().Timeout(120.Seconds()).Await(ct);

        var settled = await Mesh.GetMeshNodeStream(nodeTypePath)
            .Select(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Where(d => d is { CompilationStatus: CompilationStatus.Ok or CompilationStatus.Error })
            .FirstAsync().Timeout(300.Seconds()).Await(ct);

        Output.WriteLine($"{nodeTypePath} settled at {settled!.CompilationStatus}:\n{settled.CompilationError}");
        return settled;
    }

    private static MeshNode Space(string partition) => new(partition)
    {
        Name = partition,
        NodeType = "Space",
        State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = $"# {partition}\n\nfixture." },
    };

    private static MeshNode Code(string partition, string relativeNamespace, string id, string code) =>
        new(id, $"{partition}/{relativeNamespace}")
        {
            NodeType = CodeConventions.CodeNodeType,
            Name = id,
            State = MeshNodeState.Active,
            LastModified = DateTimeOffset.UtcNow,
            Content = new CodeConfiguration { Language = "csharp", Code = code },
        };

    private sealed class FixtureSource(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;
        public ImmutableList<MeshNode> Nodes { get; init; } = ImmutableList<MeshNode>.Empty;
        public MeshNode? Root { get; init; }
        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => Nodes;
        public MeshNode? PartitionRoot => Root;
        public IReadOnlyList<StaticContentSync> EnumerateInlineContentSyncs() => [];
    }

    /// <summary>
    /// Refuses ONE path, with a reason. The bulk verb runs the same <c>INodeValidator</c> pass the
    /// singular create runs, so the refusal reaches the importer as
    /// <c>NodeUpsertRejectionReason.ValidationFailed</c> — a verdict about the bytes.
    /// </summary>
    private sealed class RefuseOnePathValidator(Func<string?> path) : INodeValidator
    {
        public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Create];

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
            => Observable.Return(
                string.Equals(context.Node.Path, path(), StringComparison.Ordinal)
                    ? NodeValidationResult.Invalid(
                        $"'{context.Node.Path}' is refused by the test validator")
                    : NodeValidationResult.Valid());
    }
}
