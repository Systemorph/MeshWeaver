#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the AMBIGUITY rule of the mesh-wide <c>$type</c> → CLR-Type map
/// (<see cref="MeshContentTypeRegistry"/>).
///
/// <para>A runtime-compiled content type's discriminator is its bare CLR name, and that name is
/// unique only inside its own package. One customer node repo ships <c>Currency</c> in four
/// packages (<c>Reinsurance</c>, <c>ClaimsDeepfield</c>, <c>UWDeepfield</c>, <c>Ifrs17</c>) and
/// eleven more names in two or more. Under the original last-writer-wins rule the map answered
/// with whichever package's record had compiled most recently, so one package's content
/// deserialised into another package's record — members dropped, foreign defaults materialised —
/// and WHICH package won changed run to run. That is what made the plugin gate's install
/// unchanged-check nondeterministic on one image digest (Systemorph/MeshWeaver#1299).</para>
///
/// <para>The types here are built with <see cref="AssemblyBuilder"/> because that is the shape
/// under test: production never passes a <c>nodeTypePath</c>, so the declaring identity IS the
/// assembly's simple name — which, for a compiled NodeType, is derived from the NodeType path
/// and is therefore stable across rebuilds and distinct between two types that merely share a
/// short name.</para>
/// </summary>
public class MeshContentTypeRegistryTest
{
    /// <summary>A CLR type named <paramref name="typeName"/> in its own assembly — the shape a
    /// compiled NodeType produces (assembly name = the sanitized NodeType path).</summary>
    private static Type EmitType(string assemblyName, string typeName)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName(assemblyName), AssemblyBuilderAccess.RunAndCollect);
        var module = assembly.DefineDynamicModule(assemblyName);
        return module.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class)
            .CreateType();
    }

    [Fact(Timeout = 30000)]
    public void UniqueDiscriminator_Resolves()
    {
        var registry = new MeshContentTypeRegistry();
        var scenario = EmitType("SST_Scenario", "Scenario");

        registry.Register(scenario);

        registry.TryResolveByDiscriminator("Scenario", out var resolved).Should().BeTrue();
        resolved.Should().BeSameAs(scenario);
    }

    /// <summary>🚨 The defect: two packages, one short name. The map must stop answering.</summary>
    [Fact(Timeout = 30000)]
    public void TwoDeclarationsOfOneName_StopResolving()
    {
        var registry = new MeshContentTypeRegistry();
        var ifrs17 = EmitType("Ifrs17_Currency", "Currency");
        var claims = EmitType("ClaimsDeepfield_Currency", "Currency");

        registry.Register(ifrs17);
        registry.TryResolveByDiscriminator("Currency", out _)
            .Should().BeTrue("one claimant — the name is still unique");

        registry.Register(claims);

        registry.TryResolveByDiscriminator("Currency", out _)
            .Should().BeFalse(
                "answering with either package's record would deserialise the other package's " +
                "content into it, and which one wins would depend on compile order");
    }

    /// <summary>Order must not matter — the contested name is refused whichever registered first.</summary>
    [Fact(Timeout = 30000)]
    public void ContestedName_IsRefusedRegardlessOfRegistrationOrder()
    {
        var forward = new MeshContentTypeRegistry();
        forward.Register(EmitType("Ifrs17_Currency", "Currency"));
        forward.Register(EmitType("ClaimsDeepfield_Currency", "Currency"));

        var reverse = new MeshContentTypeRegistry();
        reverse.Register(EmitType("ClaimsDeepfield_Currency", "Currency"));
        reverse.Register(EmitType("Ifrs17_Currency", "Currency"));

        forward.TryResolveByDiscriminator("Currency", out _).Should().BeFalse();
        reverse.TryResolveByDiscriminator("Currency", out _).Should().BeFalse();
    }

    /// <summary>A third claimant cannot "heal" the name back into resolvability.</summary>
    [Fact(Timeout = 30000)]
    public void ContestedName_StaysContestedAfterAFurtherRegistration()
    {
        var registry = new MeshContentTypeRegistry();
        registry.Register(EmitType("Ifrs17_Currency", "Currency"));
        registry.Register(EmitType("ClaimsDeepfield_Currency", "Currency"));
        registry.Register(EmitType("Ifrs17_Currency", "Currency"));   // a rebuild of the first

        registry.TryResolveByDiscriminator("Currency", out _).Should().BeFalse();
    }

    /// <summary>
    /// A RECOMPILE of one NodeType mints a new collectible identity under the SAME assembly name.
    /// That is not a collision — the newest build is the right answer, exactly as before.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void RebuildOfTheSameDeclaration_ResolvesToTheNewestBuild()
    {
        var registry = new MeshContentTypeRegistry();
        registry.Register(EmitType("SST_Scenario", "Scenario"));
        var rebuilt = EmitType("SST_Scenario", "Scenario");

        registry.Register(rebuilt);

        registry.TryResolveByDiscriminator("Scenario", out var resolved).Should().BeTrue(
            "a rebuild is the same declaration, not a second claimant");
        resolved.Should().BeSameAs(rebuilt);
    }

    /// <summary>
    /// Two NodeTypes that share ONE compiled record (a package's shared <c>Source</c> folder)
    /// register the very same CLR type twice. One type means one answer — never ambiguous.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void SameTypeRegisteredFromTwoNodeTypes_StaysResolvable()
    {
        var registry = new MeshContentTypeRegistry();
        var shared = EmitType("SST_Shared", "Filing");

        registry.Register(shared, "SST/Filing");
        registry.Register(shared, "SST/StandReModel");

        registry.TryResolveByDiscriminator("Filing", out var resolved).Should().BeTrue();
        resolved.Should().BeSameAs(shared);
    }

    /// <summary>The exact NodeType route is unaffected — it never guessed from a name.</summary>
    [Fact(Timeout = 30000)]
    public void NodeTypeRoute_StaysExactForBothClaimants()
    {
        var registry = new MeshContentTypeRegistry();
        var ifrs17 = EmitType("Ifrs17_Currency", "Currency");
        var claims = EmitType("ClaimsDeepfield_Currency", "Currency");

        registry.Register(ifrs17, "Ifrs17/Currency");
        registry.Register(claims, "ClaimsDeepfield/Currency");

        registry.TryResolveByNodeType("Ifrs17/Currency", out var a).Should().BeTrue();
        a.Should().BeSameAs(ifrs17);
        registry.TryResolveByNodeType("ClaimsDeepfield/Currency", out var b).Should().BeTrue();
        b.Should().BeSameAs(claims);
        registry.TryResolveByDiscriminator("Currency", out _).Should().BeFalse();
    }

    /// <summary>Captures the warning lines so the collision report can be asserted on.</summary>
    private sealed class CapturingLogger : ILogger<MeshContentTypeRegistry>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    /// <summary>The collision is reported once, naming both declarations — the only place it is
    /// visible, and what an operator needs in order to rename one of the two records.</summary>
    [Fact(Timeout = 30000)]
    public void TheCollisionIsReportedOnce_NamingBothDeclarations()
    {
        var logger = new CapturingLogger();
        var registry = new MeshContentTypeRegistry(logger);

        registry.Register(EmitType("Ifrs17_Currency", "Currency"));
        registry.Register(EmitType("ClaimsDeepfield_Currency", "Currency"));

        logger.Warnings.Should().ContainSingle().Which
            .Should().Contain("Currency")
            .And.Contain("Ifrs17_Currency")
            .And.Contain("ClaimsDeepfield_Currency");
    }

    /// <summary>
    /// A REBUILD of a claimant whose name is already contested must not report the collision again
    /// — it would print "two different declarations (X and X)", a misleading line about a name whose
    /// collision was already reported, on every recompile.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void RebuildOfAContestedClaimant_DoesNotReportItAgain()
    {
        var logger = new CapturingLogger();
        var registry = new MeshContentTypeRegistry(logger);
        registry.Register(EmitType("Ifrs17_Currency", "Currency"));
        registry.Register(EmitType("ClaimsDeepfield_Currency", "Currency"));
        logger.Warnings.Clear();

        registry.Register(EmitType("Ifrs17_Currency", "Currency"));

        logger.Warnings.Should().BeEmpty(
            "a rebuild is the same declaration — there is no new collision to report");
    }

    /// <summary>
    /// The recovery seam degrades to "unresolvable" for a contested name rather than handing back
    /// a foreign record — the JsonElement the caller then keeps is honest and deterministic.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void TryRecover_RefusesAContestedDiscriminator()
    {
        var registry = new MeshContentTypeRegistry();
        registry.Register(EmitType("Ifrs17_Currency", "Currency"));
        registry.Register(EmitType("ClaimsDeepfield_Currency", "Currency"));

        var element = JsonSerializer.Deserialize<JsonElement>("""{"$type":"Currency"}""");

        registry.TryRecover(element, JsonSerializerOptions.Default).Should().BeNull();
    }
}
