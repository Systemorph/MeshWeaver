using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>Unrelated registered types must not allocate a predicate per metadata resolution.</summary>
public class PolymorphicResolverAllocationTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Probe(string Value);

    [JsonPolymorphic]
    [JsonDerivedType(typeof(DeclaredSubtype), "declared")]
    private abstract record PolymorphicBase;

    private record DeclaredSubtype(string Value) : PolymorphicBase;

    private record RegisteredSubtype(string Value) : PolymorphicBase;

    [Fact]
    public void AttributeDiscriminatorWinsOverRegistryAlias_AndOtherSubtypeStillRoundTrips()
    {
        var registry = GetHost().ServiceProvider.GetRequiredService<ITypeRegistry>();
        registry.WithType(typeof(DeclaredSubtype), "registry-alias");
        registry.WithType(typeof(RegisteredSubtype), "registered");
        var options = new JsonSerializerOptions { TypeInfoResolver = new PolymorphicTypeInfoResolver(registry) };
        var info = options.GetTypeInfo(typeof(PolymorphicBase));
        info.PolymorphismOptions!.DerivedTypes.Should().HaveCount(2);
        foreach (PolymorphicBase value in new PolymorphicBase[]
                 { new DeclaredSubtype("attribute"), new RegisteredSubtype("registry") })
        {
            var json = JsonSerializer.Serialize(value, options);
            json.Should().Contain(value is DeclaredSubtype ? "\"$type\":\"declared\"" : "\"$type\":\"registered\"");
            JsonSerializer.Deserialize<PolymorphicBase>(json, options).Should().Be(value);
        }
    }

    [Fact]
    public void UnrelatedRegistryEntries_DoNotAllocateAClosurePerCandidate()
    {
        var registry = GetHost().ServiceProvider.GetRequiredService<ITypeRegistry>();
        registry.WithType(typeof(Probe), nameof(Probe));
        var resolver = new PolymorphicTypeInfoResolver(registry);
        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        var small = SettledResolutionBytes(resolver, options, registry);

        const int candidates = 512;
        var module = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("UnrelatedResolutionCandidates"), AssemblyBuilderAccess.RunAndCollect)
            .DefineDynamicModule("Candidates");
        for (var i = 0; i < candidates; i++)
        {
            var type = module.DefineType($"Unrelated{i}", TypeAttributes.Public | TypeAttributes.Class)
                .CreateType()!;
            registry.WithType(type, type.Name);
        }

        var large = SettledResolutionBytes(resolver, options, registry);
        var added = large - small;
        Output.WriteLine($"Extra resolution allocation beyond registry traversal: {added:N0} bytes "
            + $"for {candidates} unrelated types (small={small:N0}, large={large:N0}).");
        added.Should().BeLessThan(1024L,
            "unrelated candidates need an assignability check, not a captured Any predicate "
            + "and boxed list enumerator for each registry entry");
        resolver.GetTypeInfo(typeof(Probe), options).PolymorphismOptions!.DerivedTypes
            .Should().ContainSingle().Which.DerivedType.Should().Be(typeof(Probe));
    }

    /// <summary>
    /// The resolver's allocation per resolution beyond the registry traversal, once the JIT has
    /// settled: the MINIMUM over repeated samples. Tier-0 code allocates what optimized code does
    /// not (an enumerator or closure the optimizing JIT keeps on the stack), and on a loaded runner
    /// tier-up is late — measured on CI: the same tree read 0 bytes alone and 2,460 / 55,150 bytes
    /// while a heavy suite shared the shard. A real per-candidate allocation is present in EVERY
    /// sample, so the minimum still shows it; a transient one is gone from at least one.
    ///
    /// <para>🚨 The two readings are settled SEPARATELY and subtracted once — never the minimum of
    /// per-sample DIFFERENCES. A transient (tier-0, a late tier-up) is just as likely in the
    /// traversal reading as in the resolution one, and a sample whose TRAVERSAL is inflated makes
    /// its difference strongly negative; the minimum then picks exactly that sample. Measured on
    /// core main (run 37271740357): <c>small=-7,284</c> — a negative "allocation" — so
    /// <c>large - small</c> read 15,076 bytes and the test failed with no per-candidate allocation
    /// present. Each minimum is bounded below by the true settled cost, so their difference is not
    /// pulled below it by noise in the other reading.</para>
    /// </summary>
    private static long SettledResolutionBytes(
        PolymorphicTypeInfoResolver resolver, JsonSerializerOptions options, ITypeRegistry registry)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var resolution = long.MaxValue;
        var traversal = long.MaxValue;
        for (var sample = 0; sample < 10 || (resolution - traversal > 0 && clock.ElapsedMilliseconds < 3000); sample++)
        {
            resolution = Math.Min(resolution, ResolutionBytes(resolver, options));
            traversal = Math.Min(traversal, TraversalBytes(registry));
        }
        return resolution - traversal;
    }

    private static long ResolutionBytes(PolymorphicTypeInfoResolver resolver, JsonSerializerOptions options)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
            resolver.GetTypeInfo(typeof(Probe), options);
        return (GC.GetAllocatedBytesForCurrentThread() - before) / 20;
    }

    // Registry enumeration itself materialises a DistinctBy set. Subtract that same real traversal
    // so this reading isolates the resolver's work rather than imposing a new registry contract.
    private static long TraversalBytes(ITypeRegistry registry)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
            foreach (var entry in registry.Types)
                GC.KeepAlive(entry.Value);
        return (GC.GetAllocatedBytesForCurrentThread() - before) / 20;
    }
}
