using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver.Plugins#3518: the live swap of MeshWeaver.AI failed because a dependent module's
/// generation N+1 could not build its root services — <c>AddHttpClient&lt;WebSearchPlugin&gt;()</c>
/// was refused with "already has a registered client with the name 'WebSearchPlugin'". Generation N's
/// registration had been written into the ROOT's <c>HttpClientMappingRegistry</c> instance, which every
/// probe shared by reference, and N+1's <c>WebSearchPlugin</c> is a different <see cref="Type"/> (another
/// load context) under the same type-name-derived client name.
///
/// <para>Reproduced exactly as <see cref="ModuleContexts.PrepareServices"/> does it: generation N is
/// probed against the root prefix, then generation N+1 against the SAME prefix. Negative control: a
/// module that collides with a PLATFORM client of the same name is still refused, as at boot.</para>
/// </summary>
public sealed class ALiveSwapReRegistersATypedHttpClientTest
{
    private static readonly MethodInfo AddTypedClient = typeof(HttpClientFactoryServiceCollectionExtensions)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(HttpClientFactoryServiceCollectionExtensions.AddHttpClient)
                     && m.IsGenericMethodDefinition
                     && m.GetGenericArguments().Length == 1
                     && m.GetParameters().Length == 1);

    [Fact]
    public void GenerationNPlusOne_RegistersTheSameTypedClient_AndTheRootRegistryHoldsNeitherGeneration()
    {
        var root = new ServiceCollection();
        root.AddHttpClient(); // the platform's own HttpClient infrastructure, registered before modules install
        var prefix = root.ToArray();

        var generationN = Load("MeshWeaver.AI.WebSearch", out var contextN);
        var generationNPlusOne = Load("MeshWeaver.AI.WebSearch", out var contextNPlusOne);
        try
        {
            using var n = ModuleServices.Probe("MeshWeaver.AI.WebSearch", contextN, [Register(generationN)], prefix);
            n.Blockers.Should().BeEmpty();

            // The swap: N+1's delegates run against the prefix generation N saw (old.Prefix — the
            // same root descriptors). Each generation's typed client must be recorded in a registry of
            // its own probe, never the root's; before the fix this threw "already has a registered
            // client with the name 'WebSearchPlugin'".
            using var nPlusOne = ModuleServices.Probe("MeshWeaver.AI.WebSearch", contextNPlusOne,
                [Register(generationNPlusOne)], prefix);
            nPlusOne.Blockers.Should().BeEmpty();

            NamedClients(root).Keys.Cast<string>().Should().NotContain("WebSearchPlugin",
                "the root's registry must not hold a module generation's type — it would pin that collectible context");
        }
        finally
        {
            contextN.Unload();
            contextNPlusOne.Unload();
        }
    }

    [Fact]
    public void AModuleClientNamedLikeAPlatformClient_IsStillRefused()
    {
        var root = new ServiceCollection();
        root.AddHttpClient<WebSearchPlugin>(); // a PLATFORM type that happens to share the name
        var module = Load("MeshWeaver.AI.WebSearch", out var context);
        try
        {
            var refused = Assert.Throws<InvalidOperationException>(
                () => ModuleServices.Probe("MeshWeaver.AI.WebSearch", context, [Register(module)], root.ToArray()));
            refused.Message.Should().Contain("already has a registered client with the name 'WebSearchPlugin'");
        }
        finally
        {
            context.Unload();
        }
    }

    private static Func<IServiceCollection, IServiceCollection> Register(Type client) =>
        services =>
        {
            try
            {
                AddTypedClient.MakeGenericMethod(client).Invoke(null, [services]);
            }
            catch (TargetInvocationException invocation) when (invocation.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(invocation.InnerException).Throw();
            }
            return services;
        };

    private static IDictionary NamedClients(IServiceCollection services)
    {
        var registry = services.Single(d => d.ServiceType.FullName == "Microsoft.Extensions.DependencyInjection.HttpClientMappingRegistry")
            .ImplementationInstance!;
        return (IDictionary)registry.GetType()
            .GetProperty("NamedClientRegistrations", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(registry)!;
    }

    private static Type Load(string assemblyName, out AssemblyLoadContext context)
    {
        var compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(
                "namespace MeshWeaver.AI.Plugins; public sealed class WebSearchPlugin(System.Net.Http.HttpClient http) { public System.Net.Http.HttpClient Http { get; } = http; }")],
            PlatformReferences.Platform(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var emitted = compilation.Emit(buffer);
        emitted.Success.Should().BeTrue(string.Join("; ", emitted.Diagnostics));
        buffer.Position = 0;
        context = new AssemblyLoadContext(assemblyName, isCollectible: true);
        return context.LoadFromStream(buffer).GetType("MeshWeaver.AI.Plugins.WebSearchPlugin")
            ?? throw new InvalidOperationException($"WebSearchPlugin is not in the emitted {assemblyName}");
    }

    /// <summary>A non-module type named like the module's client.</summary>
    private sealed class WebSearchPlugin(HttpClient http)
    {
        public HttpClient Http { get; } = http;
    }
}
