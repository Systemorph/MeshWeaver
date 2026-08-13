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
/// under test: the declaring identity falls back to the assembly's simple name — which, for a
/// compiled NodeType, is derived from the NodeType path and is therefore stable across rebuilds and
/// distinct between two types that merely share a short name.</para>
///
/// <para>🚨 <b>The name rule is a fallback now, not the whole story.</b> It used to be everything,
/// because production never passed a <c>nodeTypePath</c> — <c>MeshDataSource.WithContentType</c>
/// called <c>Register(dataType)</c> with no path, so <c>TryResolveByNodeType</c> had no entries
/// outside these tests and every lookup went through the contestable name. The NodeType path now
/// reaches that call ambiently (<c>NodeTypePathHolder</c>), so the EXACT route carries production
/// traffic and the name route only covers what it cannot reach: a node with no NodeType, or a type
/// whose hub has not activated yet. Refusing a contested name is still right — but a refusal leaves
/// consumers reading a default-valued record, which is why the exact route had to be wired rather
/// than merely kept (see <see cref="TryRecoverForNodeType_ResolvesBothClaimantsOfAContestedName"/>).</para>
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

    /// <summary>
    /// 🚨 The EXACT route resolves what the name route must refuse — and this is the whole reason it
    /// exists. Refusing a contested name is right (a wrong answer is silently lossy), but a refusal
    /// leaves every consumer reading <c>Content as T ?? new T()</c> a DEFAULT record. Eleven content
    /// types in one customer repo are in exactly that state today, including <c>UWDeepfieldHome</c>,
    /// whose layout area therefore renders defaults in production while its tests stay green.
    ///
    /// <para>Keyed on the node's own NodeType path there is nothing to contest: each package's nodes
    /// resolve to that package's record.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public void TryRecoverForNodeType_ResolvesBothClaimantsOfAContestedName()
    {
        var registry = new MeshContentTypeRegistry();
        var ifrs17 = EmitTypeWithProperty("Ifrs17_Currency", "Currency", "Code");
        var claims = EmitTypeWithProperty("ClaimsDeepfield_Currency", "Currency", "Code");
        registry.Register(ifrs17, "Ifrs17/Currency");
        registry.Register(claims, "ClaimsDeepfield/Currency");

        var element = JsonSerializer.Deserialize<JsonElement>("""{"$type":"Currency","Code":"CHF"}""");

        // The name route refuses — correctly, and that is what leaves consumers with a default.
        registry.TryRecover(element, JsonSerializerOptions.Default).Should().BeNull();

        // The exact route answers, and answers DIFFERENTLY per package.
        registry.TryRecoverForNodeType("Ifrs17/Currency", element, JsonSerializerOptions.Default)!
            .GetType().Should().Be(ifrs17, "the node's own NodeType names which package's record it is");
        registry.TryRecoverForNodeType("ClaimsDeepfield/Currency", element, JsonSerializerOptions.Default)!
            .GetType().Should().Be(claims);

        // …and it round-trips the payload, not merely the type tag.
        var recovered = registry.TryRecoverForNodeType(
            "Ifrs17/Currency", element, JsonSerializerOptions.Default);
        ifrs17.GetProperty("Code")!.GetValue(recovered).Should().Be("CHF");
    }

    /// <summary>
    /// A node with no NodeType, or one whose type has not activated in this process yet, is no worse
    /// off than before: the exact route falls back to the name route (which still refuses a
    /// contested name).
    /// </summary>
    [Fact(Timeout = 30000)]
    public void TryRecoverForNodeType_FallsBackToTheNameRoute_WhenThePathIsAbsentOrUnknown()
    {
        var registry = new MeshContentTypeRegistry();
        var scenario = EmitTypeWithProperty("SST_Scenario", "Scenario", "Code");
        registry.Register(scenario, "SST/Scenario");

        var element = JsonSerializer.Deserialize<JsonElement>("""{"$type":"Scenario","Code":"base"}""");

        registry.TryRecoverForNodeType(null, element, JsonSerializerOptions.Default)!
            .GetType().Should().Be(scenario, "no NodeType in hand — the uncontested name still resolves");
        registry.TryRecoverForNodeType("SST/NeverRegistered", element, JsonSerializerOptions.Default)!
            .GetType().Should().Be(scenario, "an unregistered path falls back rather than failing");
    }

    /// <summary>
    /// 🚨 The exact route must not reshape content into a type its OWN <c>$type</c> contradicts.
    ///
    /// <para><see cref="MeshContentTypeRegistry.Register"/> is last-writer-wins on the NodeType key,
    /// so one wrong writer poisons the entry for every reader — and because
    /// System.Text.Json ignores members the target does not declare and materialises defaults for
    /// the ones it does, deserialising into the wrong record SUCCEEDS. The recovery then returns a
    /// plausible, wrong object rather than failing, which is exactly how Systemorph/MeshWeaver#1379
    /// stayed invisible: a <c>Store/Plugin</c> instance served its type's
    /// <c>NodeTypeDefinition</c> as its own content, at its unchanged Version, on every read, and
    /// the paid install reported "nothing to install".</para>
    ///
    /// <para>The poisoning writer is fixed at its source (<c>MeshNodeHubFactory</c> no longer stamps
    /// a definition node's own path). This pins the invariant that keeps the NEXT such mismatch
    /// loud: an unresolvable read leaves a <see cref="JsonElement"/> the consumer's
    /// <c>ContentAs&lt;T&gt;</c> can still recover and the seams already warn about; a wrongly-typed
    /// one is silent and lossy.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public void TryRecoverForNodeType_RefusesATypeTheContentsOwnDiscriminatorContradicts()
    {
        var registry = new MeshContentTypeRegistry();
        var declaration = EmitTypeWithProperty("Store_Plugin_Declaration", "PluginContent", "Id");
        var foreign = EmitTypeWithProperty("Graph_Definition", "NodeTypeDefinition", "Description");

        // The poisoned entry: the key belongs to Store/Plugin's INSTANCES, the value does not.
        registry.Register(foreign, "Store/Plugin");

        var element = JsonSerializer.Deserialize<JsonElement>("""{"$type":"PluginContent","Id":"pack-1"}""");

        registry.TryRecoverForNodeType("Store/Plugin", element, JsonSerializerOptions.Default)
            .Should().BeNull(
                "the content says it is a PluginContent — materialising it as a NodeTypeDefinition "
                + "would succeed and be silently wrong, which is worse than not answering");

        // The same guard must NOT cost the exact route its purpose: once the correct writer wins,
        // a discriminator two packages share still resolves through the NodeType key.
        registry.Register(declaration, "Store/Plugin");
        registry.TryRecoverForNodeType("Store/Plugin", element, JsonSerializerOptions.Default)!
            .GetType().Should().Be(declaration);
    }

    /// <summary>
    /// Content written WITHOUT a <c>$type</c> has nothing to contradict, so the NodeType route stays
    /// the only answer available and must still give it. Pinned separately because the obvious way
    /// to write the guard above — "require a matching discriminator" — would silently stop typing
    /// every such node.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void TryRecoverForNodeType_StillResolvesContentThatCarriesNoDiscriminator()
    {
        var registry = new MeshContentTypeRegistry();
        var scenario = EmitTypeWithProperty("SST_Untagged", "Scenario", "Code");
        registry.Register(scenario, "SST/Scenario");

        var element = JsonSerializer.Deserialize<JsonElement>("""{"Code":"base"}""");

        var recovered = registry.TryRecoverForNodeType("SST/Scenario", element, JsonSerializerOptions.Default);
        recovered!.GetType().Should().Be(scenario, "an absent discriminator contradicts nothing");
        scenario.GetProperty("Code")!.GetValue(recovered).Should().Be("base");
    }

    /// <summary>
    /// 🚨 A recovery that cannot carry part of its input must SAY SO (issue #1388).
    ///
    /// <para>This is the third way the exact route can land on a wrong target, and the only one it
    /// cannot detect from types alone: the NodeType simply declares a content type its instances do
    /// not use. The other two are refused outright — an ambiguous discriminator, and one that
    /// contradicts the declaration — but a declaration nobody instantiates looks identical to a
    /// correct one, and System.Text.Json makes the mismatch SUCCEED: it ignores whatever the target
    /// does not declare, so the read returns a plausible object with authored values missing, no
    /// exception, nothing to grep. Three sample Article NodeTypes were in exactly that state, and
    /// every article created without an explicit <c>$type</c> lost its <c>abstract</c> — the one
    /// field the declared type had no member for.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public void ARecoveryThatDropsAuthoredMembers_IsReportedNamingThem()
    {
        var logger = new CapturingLogger();
        var registry = new MeshContentTypeRegistry(logger);
        var article = EmitTypeWithProperty("SST_Lossy", "Article", "Title");
        registry.Register(article, "SST/Article");

        var recovered = registry.TryRecoverForNodeType(
            "SST/Article",
            JsonSerializer.Deserialize<JsonElement>("""{"Title":"t","abstract":"a","authors":["x"]}"""),
            JsonSerializerOptions.Default);

        recovered.Should().NotBeNull(
            "reported, not refused: content written against an EARLIER version of the RIGHT type "
            + "legitimately carries members it has since lost, and refusing those would turn "
            + "cosmetic drift into an empty render");
        logger.Warnings.Should().ContainSingle().Which
            .Should().Contain("Article")
            .And.Contain("abstract")
            .And.Contain("authors");
    }

    /// <summary>
    /// The counter-case, so the report cannot become noise: when the target CAN represent
    /// everything, nothing is said. Pinning this is what keeps the warning meaningful — a line that
    /// fires on every healthy read is one nobody reads.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void ARecoveryThatCarriesEverything_IsSilent()
    {
        var logger = new CapturingLogger();
        var registry = new MeshContentTypeRegistry(logger);
        var scenario = EmitTypeWithProperty("SST_Lossless", "Scenario", "Code");
        registry.Register(scenario, "SST/Scenario");

        registry.TryRecoverForNodeType(
            "SST/Scenario",
            JsonSerializer.Deserialize<JsonElement>("""{"$type":"Scenario","Code":"base"}"""),
            JsonSerializerOptions.Default).Should().NotBeNull();

        logger.Warnings.Should().BeEmpty("$type is the discriminator, not an authored member");
    }

    /// <summary>A type with one settable string property — enough to prove the payload round-trips.</summary>
    private static Type EmitTypeWithProperty(string assemblyName, string typeName, string propertyName)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName(assemblyName), AssemblyBuilderAccess.RunAndCollect);
        var module = assembly.DefineDynamicModule(assemblyName);
        var typeBuilder = module.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class);
        var field = typeBuilder.DefineField($"_{propertyName}", typeof(string), FieldAttributes.Private);
        var property = typeBuilder.DefineProperty(propertyName, PropertyAttributes.None, typeof(string), null);
        const MethodAttributes accessors =
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
        var getter = typeBuilder.DefineMethod($"get_{propertyName}", accessors, typeof(string), Type.EmptyTypes);
        var gil = getter.GetILGenerator();
        gil.Emit(OpCodes.Ldarg_0);
        gil.Emit(OpCodes.Ldfld, field);
        gil.Emit(OpCodes.Ret);
        var setter = typeBuilder.DefineMethod($"set_{propertyName}", accessors, null, [typeof(string)]);
        var sil = setter.GetILGenerator();
        sil.Emit(OpCodes.Ldarg_0);
        sil.Emit(OpCodes.Ldarg_1);
        sil.Emit(OpCodes.Stfld, field);
        sil.Emit(OpCodes.Ret);
        property.SetGetMethod(getter);
        property.SetSetMethod(setter);
        return typeBuilder.CreateType()!;
    }
}
