#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.GitSync;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Ask 3, decided PURELY: a package declares what it needs, and the ENVIRONMENT'S SERVICE GRAPH —
/// not a config key the package invented — supplies it. These assertions pin the exact keys, because
/// the keys are the contract with two different deployment routes at once: Aspire's
/// <c>WithReference</c> injects <c>ConnectionStrings__x</c> and
/// <c>services__x__https__0</c>, and the Helm chart / Key Vault CSI mount inject the same names.
///
/// <para>🚨 The gate FAILS CLOSED and names what to provision. A missing parameter that installed
/// anyway would leave content erroring at first use with nothing pointing back at the missing key,
/// and a missing parameter that silently SKIPPED would be the trapdoor shape AGENTS.md forbids:
/// "the gate never ran" and "the gate passed" must never look the same.</para>
/// </summary>
public class PackageParameterResolutionTest
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static PackageManifest Package(params PackageParameter[] parameters) =>
        new() { Id = "finance-pack", Parameters = [.. parameters] };

    [Fact]
    public void AConnectionStringResolvesFromTheAspireInjectedName()
    {
        // Exactly what `WithReference(db)` puts on the container: ConnectionStrings__warehouse.
        var parameter = new PackageParameter { Name = "warehouse" };
        PackageParameters.ConfigKey(parameter).Should().Be("ConnectionStrings:warehouse");
        PackageParameters.EnvironmentVariable(parameter).Should().Be("ConnectionStrings__warehouse");
        PackageParameters.Resolve(Config(("ConnectionStrings:warehouse", "Host=pg")), parameter)
            .Should().Be("Host=pg");
    }

    [Fact]
    public void AnEndpointResolvesThroughSERVICEDISCOVERY_httpsFirst()
    {
        // The Microsoft.Extensions.ServiceDiscovery shape — already referenced and registered in
        // Memex.Portal.ServiceDefaults — which is what makes "route to another service" expressible
        // without the package knowing how the environment wires it.
        var parameter = new PackageParameter { Name = "crm", Kind = PackageParameterKind.Endpoint };
        PackageParameters.EnvironmentVariable(parameter).Should().Be("Services__crm__https__0");

        PackageParameters.Resolve(Config(
            ("Services:crm:http:0", "http://crm"),
            ("Services:crm:https:0", "https://crm")), parameter).Should().Be("https://crm");
        // Several endpoints on one scheme resolve to index 0 DETERMINISTICALLY — not to whichever
        // one the provider happens to enumerate first.
        PackageParameters.Resolve(Config(
            ("Services:crm:https:1", "https://crm-b"),
            ("Services:crm:https:0", "https://crm-a")), parameter).Should().Be("https://crm-a");
        // …falling back through the schemes Aspire may publish instead.
        PackageParameters.Resolve(Config(("Services:crm:http:0", "http://crm")), parameter)
            .Should().Be("http://crm");
        PackageParameters.Resolve(Config(("Services:crm:default:0", "crm:8080")), parameter)
            .Should().Be("crm:8080");
        // …and a deployment that publishes a single URL as a plain leaf is honoured too.
        PackageParameters.Resolve(Config(("Services:crm", "https://crm")), parameter)
            .Should().Be("https://crm");
    }

    [Fact]
    public void AValueResolvesFromTheParametersSection()
    {
        var parameter = new PackageParameter { Name = "apiKey", Kind = PackageParameterKind.Value };
        PackageParameters.EnvironmentVariable(parameter).Should().Be("Parameters__apiKey");
        PackageParameters.Resolve(Config(("Parameters:apiKey", "sk-1")), parameter).Should().Be("sk-1");
    }

    [Fact]
    public void ServiceOverridesName_SoAPackagesOwnVocabularyNeedNotBeTheDeploymentS()
    {
        var parameter = new PackageParameter { Name = "warehouse", Service = "memex" };
        PackageParameters.EnvironmentVariable(parameter).Should().Be("ConnectionStrings__memex");
        PackageParameters.Resolve(Config(("ConnectionStrings:memex", "Host=pg")), parameter)
            .Should().Be("Host=pg");
    }

    [Fact]
    public void ABLANKValueIsNotSupplied()
    {
        // The chart renders an empty string for an unset key (`{{ .Values.x | default "" }}`), so
        // "the key exists" is not the question — "is there a value" is. Treating "" as supplied is
        // precisely the half-configured install the gate exists to prevent.
        PackageParameters.Missing(
                Config(("ConnectionStrings:warehouse", "")), Package(new PackageParameter { Name = "warehouse" }))
            .Should().ContainSingle();
    }

    [Fact]
    public void RequiredIsTheDEFAULT_AndAMissingOneRefusesNamingTheEnvVar()
    {
        var package = Package(new PackageParameter
        {
            Name = "warehouse",
            Description = "The Snowflake warehouse this pack reads.",
        });
        var missing = PackageParameters.Missing(Config(), package);

        missing.Should().ContainSingle().Which.Optional.Should().BeFalse(
            "an author opts OUT of the gate; they must never have to opt in");

        var explanation = PackageParameters.Explain(package, missing);
        explanation.Should().Contain("finance-pack");
        explanation.Should().Contain("ConnectionStrings__warehouse",
            "the refusal has to carry the exact string an operator pastes into a values file");
        explanation.Should().Contain("The Snowflake warehouse this pack reads.");
        explanation.Should().Contain("Nothing was installed.");
    }

    [Fact]
    public void AnOptionalParameterDoesNotRefuse()
    {
        PackageParameters.Missing(
                Config(),
                Package(new PackageParameter { Name = "apiKey", Optional = true }))
            .Should().BeEmpty();
    }

    [Fact]
    public void APackageDeclaringNothing_IsUnaffected()
    {
        // Every package that existed before this field. The gate must be invisible to them — on a
        // mesh with no configuration at all, too.
        PackageParameters.Missing(Config(), new PackageManifest { Id = "plain" }).Should().BeEmpty();
        PackageParameters.Missing(null, new PackageManifest { Id = "plain" }).Should().BeEmpty();
        PackageParameters.Missing(null, null).Should().BeEmpty();
    }

    [Fact]
    public void NOCONFIGURATION_SuppliesNothing_SoADeclaredRequirementStillRefuses()
    {
        // Fail CLOSED: an environment that supplies nothing is not an environment that supplies
        // everything.
        PackageParameters.Missing(null, Package(new PackageParameter { Name = "warehouse" }))
            .Should().ContainSingle();
    }

    [Fact]
    public void EveryMissingParameterIsNamed_NotJustTheFirst()
    {
        // An operator fixes these one values-file line at a time; reporting only the first turns one
        // deploy into three.
        var package = Package(
            new PackageParameter { Name = "warehouse" },
            new PackageParameter { Name = "crm", Kind = PackageParameterKind.Endpoint },
            new PackageParameter { Name = "apiKey", Kind = PackageParameterKind.Value });

        var explanation = PackageParameters.Explain(
            package, PackageParameters.Missing(Config(), package));

        explanation.Should().Contain("ConnectionStrings__warehouse");
        explanation.Should().Contain("Services__crm__https__0");
        explanation.Should().Contain("Parameters__apiKey");
    }

    [Fact]
    public void ParametersRoundTripThroughANodeRepoRootDeclaration()
    {
        // Dead metadata is the recurring defect here (preInstalled, publicSegments, contactEmail all
        // shipped unread). The listing has to carry the field, or the gate never fires.
        var root = new RepoFile("Finance/index.json",
            """
            {"$type":"MeshNode","id":"Finance","namespace":"","path":"Finance",
             "mainNode":"Finance","name":"Finance","nodeType":"Space","state":"Active",
             "content":{"$type":"PluginManifest","description":"Warehouse reports.",
                        "parameters":[
                          {"name":"warehouse","kind":"ConnectionString",
                           "description":"The warehouse this pack reads."},
                          {"name":"crm","kind":"Endpoint"},
                          {"name":"apiKey","kind":"Value","optional":true},
                          "not-an-object"]}}
            """);
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-params", [root]));
        var source = new NodeRepoPackageSource(fetch, "https://github.com/acme/plugins");

        var listed = source.ListPackages("HEAD").FirstAsync().Wait().Single();

        // A malformed entry is dropped; the readable ones survive.
        listed.Parameters.Select(p => p.Name).Should().Equal(new[] { "warehouse", "crm", "apiKey" });
        listed.Parameters[1].Kind.Should().Be(PackageParameterKind.Endpoint);
        listed.Parameters[2].Optional.Should().BeTrue();
        PackageParameters.Missing(Config(), listed).Should().HaveCount(2,
            "the optional one never refuses");
    }
}
