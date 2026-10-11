using System;
using System.Collections.Generic;
using System.Linq;
using Memex.Portal.ServiceDefaults;
using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// A build principal DECLARED in configuration (policy <c>declared-not-derived</c>): the rules a
/// declaration must satisfy, and what <c>/health</c> says about it.
///
/// <para>🚨 <b>A declaration is stricter than a node.</b> Every refusal below starts from the
/// declaration that IS accepted and moves exactly one thing, so a passing assertion can only mean
/// that one rule was checked — and each refused declaration is asserted to be trusted for nothing,
/// never narrowed into something that passes.</para>
/// </summary>
public class DeclaredBuildPrincipalTest
{
    private const string Repository = "Systemorph/MeshWeaver";
    private const string Audience = "https://instance.declared.test";
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The designed declaration, in the key shape a portal's environment produces
    /// (<c>__</c> already folded to <c>:</c>).</summary>
    private static Dictionary<string, string?> Designed(int index = 0, string repository = Repository)
    {
        var prefix = $"{DeclaredBuildPrincipals.ConfigSection}:{index}";
        return new Dictionary<string, string?>
        {
            [$"{prefix}:Repository"] = repository,
            [$"{prefix}:RepositoryId"] = "743606761",
            [$"{prefix}:RepositoryOwnerId"] = "77832550",
            [$"{prefix}:Events:workflow_run:0"] = "verify",
            [$"{prefix}:Events:workflow_dispatch:0"] = "verify",
            [$"{prefix}:EventRefs:workflow_run:0"] = "refs/heads/main",
            [$"{prefix}:EventRefs:workflow_dispatch:0"] = "refs/heads/main",
            [$"{prefix}:Scopes:0"] = "verify:combo",
        };
    }

    private static IConfiguration Config(Dictionary<string, string?> values, bool audience = true)
    {
        var all = new Dictionary<string, string?>(values);
        if (audience)
            all[DeclaredBuildPrincipals.AudienceConfigKey] = Audience;
        return new ConfigurationBuilder().AddInMemoryCollection(all).Build();
    }

    private static Dictionary<string, string?> With(
        Dictionary<string, string?> values, string suffix, string? value)
    {
        var copy = new Dictionary<string, string?>(values);
        var key = $"{DeclaredBuildPrincipals.ConfigSection}:0:{suffix}";
        if (value is null)
            copy.Remove(key);
        else
            copy[key] = value;
        return copy;
    }

    private static GitHubBuildClaims Claims(
        string repository = Repository,
        string eventName = "workflow_run",
        string gitRef = "refs/heads/main",
        string repositoryId = "743606761",
        string ownerId = "77832550") =>
        new()
        {
            Repository = repository,
            RepositoryId = repositoryId,
            RepositoryOwnerId = ownerId,
            EventName = eventName,
            Ref = gitRef,
        };

    [Fact]
    public void TheDesignedDeclaration_IsAccepted_AndAllowsExactlyWhatItDeclares()
    {
        var declarations = DeclaredBuildPrincipals.Read(Config(Designed()));

        Assert.Empty(declarations.Refused);
        var declared = Assert.Single(declarations.Accepted);
        Assert.Equal($"{DeclaredBuildPrincipals.ConfigSection}:0", declared.ConfigPath);
        var principal = declared.Principal;

        // The designed case, on both declared events.
        Assert.True(principal.Allows(Claims(), BuildVerbs.Verify, "combo", Now));
        Assert.True(principal.Allows(Claims(eventName: "workflow_dispatch"), BuildVerbs.Verify, "combo", Now));

        // …and one thing moved at a time.
        Assert.False(principal.Allows(Claims(eventName: "push"), BuildVerbs.Verify, "combo", Now));
        Assert.False(principal.Allows(Claims(gitRef: "refs/heads/feature"), BuildVerbs.Verify, "combo", Now));
        Assert.False(principal.Allows(Claims(repositoryId: "1"), BuildVerbs.Verify, "combo", Now));
        Assert.False(principal.Allows(Claims(ownerId: "1"), BuildVerbs.Verify, "combo", Now));
        Assert.False(principal.Allows(Claims(repository: "Systemorph/MeshWeaver.Evil"), BuildVerbs.Verify, "combo", Now));
        Assert.False(principal.Allows(Claims(), BuildVerbs.Fetch, "plugins", Now));
        Assert.False(principal.Allows(Claims(), BuildVerbs.Publish, "combo", Now));
    }

    [Fact]
    public void Find_MatchesTheRepositoryExactly_NeverAsAPrefix()
    {
        var declarations = DeclaredBuildPrincipals.Read(Config(Designed()));

        Assert.NotNull(declarations.Find("systemorph/meshweaver"));
        Assert.Null(declarations.Find("Systemorph/MeshWeaver.Plugins"));
        Assert.Null(declarations.Find("Systemorph/Mesh"));
        Assert.Null(declarations.Find(null));
    }

    public static TheoryData<string, string?, string> Refusals => new()
    {
        { "Repository", "NotARepository", "owner/name" },
        { "Repository", null, "owner/name" },
        { "RepositoryId", null, "RepositoryId" },
        { "RepositoryId", "not-a-number", "RepositoryId" },
        { "RepositoryOwnerId", null, "RepositoryOwnerId" },
        { "Scopes:0", null, "Scopes is empty" },
        { "Scopes:0", "fetch:plugins", "cannot be declared in configuration" },
        { "Scopes:1", "publish:plugins", "cannot be declared in configuration" },
        { "Events:workflow_run:0", "publish", "no declared scope carries" },
        { "Events:push:0", "verify", "carries no EventRefs allow-list" },
        { "EventRefs:workflow_run:0", null, "carries no EventRefs allow-list" },
        { "EventRefs:workflow_run:0", "main", "not a full git ref" },
        { "EventRefs:pull_request:0", "refs/heads/main", "that Events does not list" },
        { "IsRevoked", "false", "unknown key" },
        { "Scope", "verify:combo", "unknown key" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void ADeclarationThatBreaksOneRule_IsRefusedWhole_AndNamed(string key, string? value, string reason)
    {
        var declarations = DeclaredBuildPrincipals.Read(Config(With(Designed(), key, value)));

        Assert.Empty(declarations.Accepted);
        var refused = Assert.Single(declarations.Refused);
        Assert.Contains(reason, refused.Reason, StringComparison.Ordinal);
        Assert.Null(declarations.Find(Repository));
    }

    [Fact]
    public void NoEventsAtAll_IsRefused()
    {
        var values = Designed().Where(p => !p.Key.Contains(":Events:", StringComparison.Ordinal))
            .ToDictionary(p => p.Key, p => p.Value);

        var declarations = DeclaredBuildPrincipals.Read(Config(values));

        Assert.Empty(declarations.Accepted);
        Assert.Contains("Events is empty", Assert.Single(declarations.Refused).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepositoryDeclaredTwice_IsTrustedThroughNeither_AndAnotherDeclarationIsUntouched()
    {
        var values = Designed(0);
        foreach (var pair in Designed(1, "systemorph/meshweaver"))
            values[pair.Key] = pair.Value;
        foreach (var pair in Designed(2, "Systemorph/Other"))
            values[pair.Key] = pair.Value;

        var declarations = DeclaredBuildPrincipals.Read(Config(values));

        Assert.Equal("Systemorph/Other", Assert.Single(declarations.Accepted).Principal.Repository);
        Assert.Equal(2, declarations.Refused.Count);
        Assert.All(declarations.Refused, r => Assert.Contains("more than once", r.Reason, StringComparison.Ordinal));
        Assert.Null(declarations.Find(Repository));
    }

    // ── /health ──────────────────────────────────────────────────────────────

    [Fact]
    public void NothingDeclared_IsHealthy_AndSaysSo()
    {
        var entry = BuildPrincipalHealthCheck.Evaluate(Config([]));

        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Contains("no build principal is DECLARED", entry.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclaredPrincipal_IsNamedInFullOnHealth()
    {
        var entry = BuildPrincipalHealthCheck.Evaluate(Config(Designed()));

        Assert.Equal(HealthStatus.Healthy, entry.Status);
        foreach (var fact in new[]
                 {
                     "1 build principal(s) DECLARED", Repository, "743606761", "77832550",
                     "workflow_run@refs/heads/main", "workflow_dispatch@refs/heads/main", "verify:combo",
                 })
            Assert.Contains(fact, entry.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", entry.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedDeclaration_IsDegraded_AndNamesTheRule()
    {
        var entry = BuildPrincipalHealthCheck.Evaluate(Config(With(Designed(), "Scopes:0", "fetch:plugins")));

        Assert.Equal(HealthStatus.Degraded, entry.Status);
        Assert.Contains("REFUSED", entry.Description, StringComparison.Ordinal);
        Assert.Contains("cannot be declared in configuration", entry.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclaredPrincipalWithNoAudience_IsDegraded_BecauseTheWholeLegIsOff()
    {
        var entry = BuildPrincipalHealthCheck.Evaluate(Config(Designed(), audience: false));

        Assert.Equal(HealthStatus.Degraded, entry.Status);
        Assert.Contains("NO audience is configured", entry.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHealthLine_PrintsAlthoughItIsHealthy()
    {
        var entry = BuildPrincipalHealthCheck.Evaluate(Config(Designed()));
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                [DeclaredBuildPrincipals.HealthCheckName] = new(
                    entry.Status, entry.Description, TimeSpan.Zero, null, null, [ProbeEndpoints.CensusTag]),
            },
            TimeSpan.Zero);

        var line = Memex.Portal.ServiceDefaults.ServiceDefaults
            .HealthBodyLines(report, Memex.Portal.ServiceDefaults.ServiceDefaults.StartupStatus(report))
            .Single(l => l.StartsWith(DeclaredBuildPrincipals.HealthCheckName + ":", StringComparison.Ordinal));

        Assert.StartsWith("build_principals: Healthy", line, StringComparison.Ordinal);
        Assert.Contains(Repository, line, StringComparison.Ordinal);
    }
}
