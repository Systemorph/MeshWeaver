using System.Collections.Generic;
using System.Collections.Immutable;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// A release request a delivery gate DECLINED (the last build keeps serving) is not a handled one: the
/// held type is judged again when the inputs its decline was formed under moved, once per distinct
/// input set, and never otherwise. The decision is <see cref="DeclinedReleaseLedger"/>; the release
/// watcher and the boot sweep both ask it.
/// </summary>
public class ADeclinedReleaseIsRejudgedWhenItsCauseMovesTest
{
    private static readonly ImmutableDictionary<string, long> Sources =
        ImmutableDictionary.CreateRange(new Dictionary<string, long> { ["Mod/Type/Source/A"] = 1 });

    private static NodeTypeDefinition Held(CompilationStatus status = CompilationStatus.Ok) => new()
    {
        CompilationStatus = status,
        BuildProvenance = BuildProvenance.StaleAdopted,
        DeliveryHoldReason = "no bundle for this identity",
        CurrentSourceVersions = Sources,
    };

    private static string Inputs(string? modulesHash, IReadOnlyDictionary<string, long>? sources = null) =>
        NodeTypeCompilationHelpers.BuildInputsToken(modulesHash, sources ?? Sources);

    [Fact]
    public void AHeldTypeNeverJudgedByThisProcessIsDue()
    {
        // A roll, or the first look after boot: the process has judged nothing yet.
        Assert.True(new DeclinedReleaseLedger().IsDue(Held(), Inputs("m1")));
    }

    [Fact]
    public void ADeclineThatDeclinesAgainUnderUnchangedInputsSettlesAndDoesNotLoop()
    {
        var ledger = new DeclinedReleaseLedger();
        var inputs = Inputs("m1");

        Assert.True(ledger.IsDue(Held(), inputs));
        Assert.True(ledger.TryClaim(inputs));       // the one re-judgement is dispatched…

        // …it declines again (the record is held, settled, byte-identical): nothing more is due.
        for (var emission = 0; emission < 5; emission++)
        {
            Assert.False(ledger.IsDue(Held(), inputs));
            Assert.False(ledger.TryClaim(inputs));
        }
    }

    [Fact]
    public void ANewPublicationOrModuleSetOrBundleMovesTheInputsAndIsRejudgedOnceThenSettles()
    {
        var ledger = new DeclinedReleaseLedger();
        Assert.True(ledger.TryClaim(Inputs("m1")));
        Assert.False(ledger.IsDue(Held(), Inputs("m1")));

        // A new bundle arrives with a new module set (hash), then a new source publication.
        foreach (var moved in new[]
                 {
                     Inputs("m2"),
                     Inputs("m2", ImmutableDictionary.CreateRange(new Dictionary<string, long> { ["Mod/Type/Source/A"] = 2 })),
                 })
        {
            Assert.True(ledger.IsDue(Held(), moved), "a changed input must re-judge the held type");
            Assert.True(ledger.TryClaim(moved));
            Assert.False(ledger.IsDue(Held(), moved), "…and settle: the same input never re-fires it");
        }
    }

    [Fact]
    public void ARequestThatReachedTheGateByTheOrdinaryRouteIsNotJudgedAgainOnTheSameInputs()
    {
        var ledger = new DeclinedReleaseLedger();
        ledger.Note(Inputs("m1"));

        Assert.False(ledger.IsDue(Held(), Inputs("m1")));
        Assert.True(ledger.IsDue(Held(), Inputs("m2")));
    }

    [Fact]
    public void ATypeThatIsNotHeldOrIsAlreadyJudgingIsNeverDue()
    {
        var ledger = new DeclinedReleaseLedger();
        var plain = new NodeTypeDefinition { CompilationStatus = CompilationStatus.Ok, BuildProvenance = BuildProvenance.Compiled };

        Assert.False(ledger.IsDue(plain, Inputs("m1")), "a type with its own build holds nothing to re-judge");
        Assert.False(ledger.IsDue(null, Inputs("m1")));
        Assert.False(ledger.IsDue(Held(CompilationStatus.Pending), Inputs("m1")), "a judgement is in flight");
        Assert.False(ledger.IsDue(Held(CompilationStatus.Compiling), Inputs("m1")));
    }

    [Fact]
    public void ARefusedBuildIsHeldToo()
    {
        var refused = Held(CompilationStatus.Unavailable) with { BuildProvenance = BuildProvenance.AdoptionRefused };
        Assert.True(new DeclinedReleaseLedger().IsDue(refused, Inputs("m1")));
    }

    [Fact]
    public void ALiftedHoldIsNoLongerDue()
    {
        // The re-judgement adopted a bundle: the record is no longer held, so nothing is due.
        var ledger = new DeclinedReleaseLedger();
        var adopted = Held() with { BuildProvenance = BuildProvenance.AdoptedVerified, DeliveryHoldReason = null };
        Assert.False(ledger.IsDue(adopted, Inputs("m9")));
    }
}
