#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 Policy <c>azure-repos-two-way</c> (MeshWeaver#6500, superseding <c>azure-repos-push-only</c>),
/// as data: an Azure Repos URL is recognised as such, an Azure Repos source is held to exactly the
/// direction rules a GitHub one is, what Azure Repos does not offer (pull requests, issues) is
/// refused BY NAME, the provider seam forwards every member, and every read and push authenticates
/// with the Azure DevOps identity DECLARED in the instance's configuration (its deployment record) —
/// never a stored secret. Every rule has a GitHub negative control beside it. The end-to-end
/// scenarios, side by side on both providers, are <c>AzureReposGitHubParityTest</c> (Hosting.Test).
/// </summary>
public class AzureReposSyncPolicyTest
{
    // ── URL recognition ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://dev.azure.com/client-a/Memex/_git/content", "client-a", "Memex", "content")]
    [InlineData("https://client-a@dev.azure.com/client-a/Memex/_git/content", "client-a", "Memex", "content")]
    [InlineData("https://dev.azure.com/client-a/My%20Project/_git/content.git", "client-a", "My Project", "content")]
    [InlineData("https://client-a.visualstudio.com/Memex/_git/content", "client-a", "Memex", "content")]
    [InlineData("https://client-a.visualstudio.com/DefaultCollection/Memex/_git/content", "client-a", "Memex", "content")]
    public void AnAzureReposUrl_IsRecognised(string url, string org, string project, string repo)
    {
        AzureReposRepository.TryParse(url, out var parsed).Should().BeTrue();
        parsed.Should().Be(new AzureReposRepository(org, project, repo));
        GitRepositoryProvider.Classify(url).Should().Be(GitRepositoryProvider.AzureRepos);
    }

    [Theory]
    [InlineData("https://github.com/Systemorph/MeshWeaver", "GitHub")]
    [InlineData("https://github.com/Systemorph/MeshWeaver.git", "GitHub")]
    [InlineData("https://dev.azure.com/client-a/Memex", "Git")]                 // no _git segment
    [InlineData("https://dev.azure.com/client-a/Memex/_git/content/extra", "Git")] // too deep
    [InlineData("http://dev.azure.com/client-a/Memex/_git/content", "Git")]     // not https
    [InlineData("https://gitlab.example.com/a/b", "Git")]
    [InlineData("/tmp/local-remote.git", "Git")]
    [InlineData("", "Git")]
    public void EveryOtherUrl_IsNotAzureRepos(string url, string expected)
    {
        AzureReposRepository.TryParse(url, out _).Should().BeFalse();
        GitRepositoryProvider.Classify(url).Should().Be(expected);
    }

    // ── Two-way: the provider adds no sync rule of its own ───────────────────

    private const string AzureUrl = "https://dev.azure.com/client-a/Memex/_git/content";
    private const string GitHubUrl = "https://github.com/Systemorph/Content";

    [Theory]
    [InlineData(SyncDirection.Bidirectional, null)]
    [InlineData(SyncDirection.ExportOnly, null)]
    [InlineData(SyncDirection.ImportOnly, "activity.gitsync.importOnlyNoExport")]
    public void TheExportGuard_IsTheSameForBothProviders(SyncDirection direction, string? expectedKey)
    {
        // Policy azure-repos-two-way: an Azure Repos source exports in every direction a GitHub one
        // does, and is refused exactly where a GitHub one is (the negative control is the GitHub row).
        AzureReposSyncPolicy.ExportGuard(new GitHubSyncConfig { RepositoryUrl = AzureUrl, Direction = direction })?.Key
            .Should().Be(expectedKey);
        AzureReposSyncPolicy.ExportGuard(new GitHubSyncConfig { RepositoryUrl = GitHubUrl, Direction = direction })?.Key
            .Should().Be(expectedKey);
    }

    [Theory]
    [InlineData(SyncDirection.Bidirectional, null)]
    [InlineData(SyncDirection.ImportOnly, null)]
    [InlineData(SyncDirection.ExportOnly, "activity.gitsync.exportOnlyNoImport")]
    public void TheReimportGuard_IsTheSameForBothProviders(SyncDirection direction, string? expectedKey)
    {
        AzureReposSyncPolicy.ReimportGuard(new GitHubSyncConfig { RepositoryUrl = AzureUrl, Direction = direction })?.Key
            .Should().Be(expectedKey);
        AzureReposSyncPolicy.ReimportGuard(new GitHubSyncConfig { RepositoryUrl = GitHubUrl, Direction = direction })?.Key
            .Should().Be(expectedKey);
    }

    [Theory]
    [InlineData(AzureReposSyncPolicy.Unsupported.PullRequest, "Pull requests")]
    [InlineData(AzureReposSyncPolicy.Unsupported.Issues, "Issues")]
    public void WhatAzureReposDoesNotOffer_IsRefusedByName(string operation, string englishLead)
    {
        var refusal = AzureReposSyncPolicy.Refuse(AzureUrl, operation);
        refusal.Should().NotBeNull();
        refusal!.Key.Should().Be("gitsync.azure.unsupported." + operation);
        refusal.Render(null).Should().StartWith(englishLead).And.Contain(AzureUrl);
        AzureReposSyncPolicy.Refuse(GitHubUrl, operation).Should().BeNull(
            "the negative control: GitHub pull requests and issues are untouched");
    }

    [Fact]
    public void TheService_RunsTheGuards_AndNoProviderRefusalOnTheSyncPath()
    {
        // The guards above are only the service's behaviour if the service calls THEM — and the
        // push-only refusals (RefuseInbound on import / re-import / branch state / branch head) are
        // gone from the sync path, not merely unused.
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(
            RepoPaths.Root, "src", "MeshWeaver.GitSync", "GitHubSyncService.cs"));
        source.Should().Contain("SyncDirectionGuards.ExportGuard(config)");
        source.Should().Contain("SyncDirectionGuards.ReimportGuard(config)");
        source.Contains("config.Direction == SyncDirection.ImportOnly", StringComparison.Ordinal)
            .Should().BeFalse("an inline import-only check in the service would bypass the guard");
        source.Contains("if (config.Direction == SyncDirection.ExportOnly)", StringComparison.Ordinal)
            .Should().BeFalse("an inline export-only check in the service would bypass the guard");
        source.Contains("AzureReposSyncPolicy.Refuse(", StringComparison.Ordinal)
            .Should().BeFalse("the sync itself refuses nothing on Azure Repos that it allows on GitHub");
    }

    [Fact]
    public void TheGenericDirectionRefusals_KeepTheirShippedExceptionTypeAndMessage()
    {
        // MeshWeaver.Plugins' SyncDirectionAndSourcesTest asserts Assert.Throws<InvalidOperationException>
        // (an EXACT type match) and the English text — for both providers now.
        foreach (var url in new[] { GitHubUrl, AzureUrl })
        {
            var export = AzureReposSyncPolicy.ExportGuard(
                new GitHubSyncConfig { RepositoryUrl = url, Direction = SyncDirection.ImportOnly })!.ToException();
            export.GetType().Should().Be(typeof(InvalidOperationException));
            export.Message.Should().Be(
                $"This sync source is import-only (repo → mesh): exporting to {url} is not allowed. " +
                "Change the source's Sync direction to Bidirectional or Export-only to commit.");

            var import = AzureReposSyncPolicy.ReimportGuard(
                new GitHubSyncConfig { RepositoryUrl = url, Direction = SyncDirection.ExportOnly })!.ToException();
            import.GetType().Should().Be(typeof(InvalidOperationException));
            import.Message.Should().Be(
                $"This sync source is export-only (mesh → repo): importing from {url} is not allowed. " +
                "Change the source's Sync direction to Bidirectional or Import-only to re-import.");
        }

        // …while the activity transcript still carries a KEY, so a viewer reads it in their language.
        var legacy = AzureReposSyncPolicy.ExportGuard(
            new GitHubSyncConfig { RepositoryUrl = GitHubUrl, Direction = SyncDirection.ImportOnly })!;
        var exception = legacy.ToException();
        var entry = legacy.ToLogMessage(Microsoft.Extensions.Logging.LogLevel.Error);
        entry.MessageKey.Should().Be("activity.gitsync.importOnlyNoExport");
        entry.Message.Should().Be(exception.Message);
        legacy.Render("en").Should().Be(exception.Message, "the English catalog entry is the shipped sentence");
        legacy.Render("de").Should().NotBe(exception.Message);
        GitSyncRefusalException.TryGet(exception, out var carried).Should().BeTrue(
            "the plain exception carries its refusal, so the activity runner can key the transcript line");
        carried.Key.Should().Be("activity.gitsync.importOnlyNoExport");
        GitSyncRefusalException.Localize(exception, "de").Should().Be(legacy.Render("de"));
        GitSyncRefusalException.TryGet(new InvalidOperationException("upstream"), out _).Should().BeFalse();

        // Negative control: an Azure Repos refusal IS the localizable type.
        AzureReposSyncPolicy.Refuse(AzureUrl, AzureReposSyncPolicy.Unsupported.PullRequest)!
            .ToException().GetType().Should().Be(typeof(GitSyncRefusalException));
    }

    [Theory]
    [InlineData("https://dev.azure.com/client-a/Memex/_git/content")]
    [InlineData("https://client-a.visualstudio.com/Memex/_git/content")]
    public void AnAzureReposUrl_HasNoGitHubIdentity(string url)
    {
        // Read positionally the URL is "client-a/Memex" — the name of some GitHub repository, whose push
        // webhook would otherwise have imported into this source.
        GitHubRepoIdentityResolver.Parse(url).Should().BeNull();
        GitHubRepoIdentityResolver.Parse(GitHubUrl)!.ToString().Should().Be("Systemorph/Content",
            "the negative control: a GitHub URL keeps its identity");
    }

    [Fact]
    public void TheRetiredPushOnlyPolicy_IsAForwarder_ThatAnswersTheCurrentRule()
    {
        // Public surface a module compiled against an earlier platform may bind: kept, obsolete, and
        // answering azure-repos-two-way — every push-only refusal is gone, pull requests still refused.
#pragma warning disable CS0618
        AzureReposPushPolicy.RefuseExport(new GitHubSyncConfig { RepositoryUrl = AzureUrl, Direction = SyncDirection.Bidirectional })
            .Should().BeNull();
        foreach (var op in new[] { AzureReposPushPolicy.InboundOperation.Import, AzureReposPushPolicy.InboundOperation.Reimport,
                     AzureReposPushPolicy.InboundOperation.CheckBranch, AzureReposPushPolicy.InboundOperation.BranchHead })
            AzureReposPushPolicy.RefuseInbound(AzureUrl, op).Should().BeNull(op);
        AzureReposPushPolicy.RefuseInbound(AzureUrl, AzureReposPushPolicy.InboundOperation.PullRequest)!.Key
            .Should().Be("gitsync.azure.unsupported.pullRequest");
        AzureReposPushPolicy.RefuseInbound(GitHubUrl, AzureReposPushPolicy.InboundOperation.PullRequest).Should().BeNull();
        AzureReposPushPolicy.ExportGuard(new GitHubSyncConfig { RepositoryUrl = AzureUrl, Direction = SyncDirection.ImportOnly })!.Key
            .Should().Be("activity.gitsync.importOnlyNoExport");
        AzureReposPushPolicy.ReimportGuard(new GitHubSyncConfig { RepositoryUrl = AzureUrl, Direction = SyncDirection.ExportOnly })!.Key
            .Should().Be("activity.gitsync.exportOnlyNoImport");
#pragma warning restore CS0618
    }

    // ── The provider seam ────────────────────────────────────────────────────

    [Fact]
    public void TheProviderSeam_ForwardsEveryInterfaceMember_DefaultedOnesIncluded()
    {
        // A default interface member the router did not declare would bind to the FALLBACK (a full
        // fetch for a head lookup, "diff unknown" for every diff) instead of the provider's own.
        var map = typeof(GitProviderRepoClient).GetInterfaceMap(typeof(IGitHubRepoClient));
        var notForwarded = map.TargetMethods
            .Where(m => m.DeclaringType != typeof(GitProviderRepoClient))
            .Select(m => m.Name)
            .ToArray();
        notForwarded.Should().BeEmpty("every member must be implemented on the router itself");
    }

    [Fact]
    public void TheProviderSeam_SendsAzureReposUrlsToTheAzureClient_AndEverythingElseToGitHub()
    {
        using var pools = new IoPoolRegistry(new IoPoolOptions());
        var azure = new AzureReposRepoClient(pools);
        var github = new AzureReposRepoClient(pools);   // any distinct instance stands for "the GitHub client"
        var router = new GitProviderRepoClient(github,
            new GitHostingProviders([new AzureReposGitHostingProvider(null, azure)]));
        router.For(AzureUrl).Should().BeSameAs(azure);
        router.For("https://client-a.visualstudio.com/DefaultCollection/Memex/_git/content").Should().BeSameAs(azure);
        router.For(GitHubUrl).Should().BeSameAs(github);
        router.For("/tmp/local-remote.git").Should().BeSameAs(github);
    }

    [Fact]
    public void TheProductionRegistration_IsTheProviderSeam()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(new IoPoolRegistry(new IoPoolOptions()));
        services.AddGitHubSyncServices();
        var descriptor = services.Last(d => d.ServiceType == typeof(IGitHubRepoClient));
        descriptor.ImplementationFactory.Should().NotBeNull();
        services.Should().Contain(d => d.ServiceType == typeof(AzureReposRepoClient));
        services.Should().Contain(d => d.ServiceType == typeof(GitHostingProviders));

        // …and the built-in Azure Repos provider is in the registry the router and the sync read.
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<GitHostingProviders>();
        registry.Classify(AzureUrl).Should().Be(GitRepositoryProvider.AzureRepos);
        registry.Classify(GitHubUrl).Should().Be(GitRepositoryProvider.GitHub);
    }

    [Fact]
    public void AContributedAzureReposProvider_ReplacesTheBuiltIn()
    {
        // The deleting half lands last: the module's provider takes the kind over while the
        // platform still ships its own copy, and nothing is ambiguous in between.
        using var pools = new IoPoolRegistry(new IoPoolOptions());
        var builtIn = new AzureReposGitHostingProvider(null, new AzureReposRepoClient(pools));
        var contributed = new AzureReposGitHostingProvider(null, new AzureReposRepoClient(pools));
        var registry = new GitHostingProviders([contributed], [builtIn]);
        registry.For(AzureUrl).Should().BeSameAs(contributed);
        registry.All.Should().ContainSingle();
    }

    [Fact]
    public void TheAzureReposProvider_RefusesPullRequestsAndIssues_AndAuthorsAsTheInstance()
    {
        using var pools = new IoPoolRegistry(new IoPoolOptions());
        IGitHostingProvider provider = new AzureReposGitHostingProvider(null, new AzureReposRepoClient(pools));
        provider.Refuse(AzureUrl, GitHostingOperation.PullRequest)!.Key.Should().Be("gitsync.azure.unsupported.pullRequest");
        provider.Refuse(AzureUrl, GitHostingOperation.Issues)!.Key.Should().Be("gitsync.azure.unsupported.issues");
        provider.Refuse(AzureUrl, "somethingElse").Should().BeNull("an operation it does not name is offered");
        (provider.CommitAuthorName, provider.CommitAuthorEmail).Should().Be(("MeshWeaver", "noreply@meshweaver.cloud"));
        provider.DisplayNameKey.Should().Be("ui.gitSync.provider.AzureRepos");
        provider.Claims(GitHubUrl).Should().BeFalse("the negative control");
    }

    // ── The REST client's pure planning ──────────────────────────────────────

    [Fact]
    public void TheGitBlobId_IsGitsOwn()
    {
        // `printf 'hello\n' | git hash-object --stdin`
        AzureReposRepoClient.GitBlobId(System.Text.Encoding.UTF8.GetBytes("hello\n"))
            .Should().Be("ce013625030ba8dba906f756967f9e9ca394464a");
        AzureReposRepoClient.GitBlobId([]).Should().Be("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391");
    }

    [Fact]
    public void TheMirror_AddsEditsAndDeletesWithinThePrefix_AndSkipsUnchangedBytes()
    {
        var same = System.Text.Encoding.UTF8.GetBytes("same");
        var existing = new[]
        {
            new AzureReposRepoClient.AzureReposItem("site/Same.json", AzureReposRepoClient.GitBlobId(same)),
            new AzureReposRepoClient.AzureReposItem("site/Changed.json", AzureReposRepoClient.GitBlobId([1])),
            new AzureReposRepoClient.AzureReposItem("site/Gone.json", AzureReposRepoClient.GitBlobId([2])),
        };
        var files = new[]
        {
            new RepoFile("Same.json", "same"),
            new RepoFile("Changed.json", "changed"),
            new RepoFile("New.bin", "", [0xFF, 0x00]),
        };

        var changes = AzureReposRepoClient.MirrorChanges("site/", existing, files);

        string.Join(",", changes.Select(c => $"{c.ChangeType}:{c.Path}"))
            .Should().Be("edit:/site/Changed.json,add:/site/New.bin,delete:/site/Gone.json");
        changes.Single(c => c.Path == "/site/New.bin").Content.Should().Equal(new byte[] { 0xFF, 0x00 },
            "binary bytes travel as they are");
        AzureReposRepoClient.MirrorChanges("site/", existing.Take(1).ToArray(), files.Take(1).ToArray())
            .Should().BeEmpty("an export that changes nothing has nothing to push");
    }

    [Fact]
    public void ThePushBody_IsOneCommit_NamingTheHeadItRead_WithBase64Content()
    {
        var body = AzureReposRepoClient.PushBody("main", "abc123",
            [new AzureReposRepoClient.MirrorChange("add", "/a.md", System.Text.Encoding.UTF8.GetBytes("hi")),
             new AzureReposRepoClient.MirrorChange("delete", "/b.md", null)],
            "Sync", "MeshWeaver", "noreply@meshweaver.cloud");

        body["refUpdates"]![0]!["name"]!.GetValue<string>().Should().Be("refs/heads/main");
        body["refUpdates"]![0]!["oldObjectId"]!.GetValue<string>().Should().Be("abc123",
            "a concurrent push to the branch is refused by Azure DevOps, never overwritten");
        var changes = body["commits"]![0]!["changes"]!.AsArray();
        changes[0]!["newContent"]!["contentType"]!.GetValue<string>().Should().Be("base64encoded");
        changes[0]!["newContent"]!["content"]!.GetValue<string>().Should().Be("aGk=");
        changes[1]!["newContent"].Should().BeNull("a delete carries no content");
    }

    [Fact]
    public void TheDiff_IsNullWheneverItIsNotAReliableDiffOfBaseToHead()
    {
        var diff = System.Text.Json.Nodes.JsonNode.Parse("""
            {"commonCommit":"base","allChangesIncluded":true,"changes":[
              {"item":{"path":"/site/a.json"},"changeType":"edit"},
              {"item":{"path":"/site/new.json"},"changeType":"rename","sourceServerItem":"/site/old.json"},
              {"item":{"path":"/site/dir","isFolder":true},"changeType":"add"},
              {"item":{"path":"/elsewhere/x.json"},"changeType":"add"}]}
            """)!;
        string.Join(",", AzureReposRepoClient.ChangedPaths(diff, "base", "site/")!)
            .Should().Be("a.json,new.json,old.json", "a rename names both its paths; folders and other subtrees do not count");

        AzureReposRepoClient.ChangedPaths(diff, "other-base", "site/").Should().BeNull(
            "a base that is not the common commit is a rewritten history — import in full");
        diff["allChangesIncluded"] = false;
        AzureReposRepoClient.ChangedPaths(diff, "base", "site/").Should().BeNull(
            "a partial change list must never read as \"nothing else changed\"");
    }

    // ── Localization of every refusal ────────────────────────────────────────

    [Theory]
    [InlineData("gitsync.azure.unsupported.pullRequest")]
    [InlineData("gitsync.azure.unsupported.issues")]
    [InlineData("gitsync.azure.repositoryNotFound")]
    [InlineData("gitsync.azure.nothingToExport")]
    [InlineData("gitsync.azure.branchMissing")]
    [InlineData("gitsync.azure.branchCreateRefused")]
    [InlineData("gitsync.azure.pushNamedNoCommit")]
    [InlineData("gitsync.azure.unresolved")]
    [InlineData("gitsync.azure.invalidSubdirectory")]
    [InlineData("gitsync.azure.failed.readRepository")]
    [InlineData("gitsync.azure.failed.readRefs")]
    [InlineData("gitsync.azure.failed.readCommit")]
    [InlineData("gitsync.azure.failed.listItems")]
    [InlineData("gitsync.azure.failed.readBlob")]
    [InlineData("gitsync.azure.failed.readDiff")]
    [InlineData("gitsync.azure.failed.createBranch")]
    [InlineData("gitsync.azure.failed.push")]
    [InlineData("gitsync.azure.identityNotDeclared")]
    [InlineData("gitsync.azure.noWorkloadIdentity")]
    [InlineData("gitsync.azure.notRegistered")]
    [InlineData("activity.gitsync.importOnlyNoExport")]
    [InlineData("activity.gitsync.exportOnlyNoImport")]
    [InlineData("ui.gitSync.pullRequestsNotOffered")]
    public void EveryRefusal_RendersInEnglishAndGerman(string key)
    {
        var refusal = GitSyncRefusal.Of(key, ("url", AzureUrl), ("direction", "Bidirectional"), ("variable", "X"));
        var en = refusal.Render("en");
        var de = refusal.Render("de");
        en.Should().NotBe(key, "the key must be in the English catalog");
        de.Should().NotBe(key, "the key must be in the German catalog");
        de.Should().NotBe(en, "the German entry must be a translation, not the English text");
        en.Should().NotContain("{url}").And.NotContain("{direction}").And.NotContain("{variable}");
    }

    [Fact]
    public void ARefusalException_RendersForTheViewer_AndAnyOtherExceptionKeepsItsMessage()
    {
        var refusal = AzureReposSyncPolicy.Refuse(AzureUrl, AzureReposSyncPolicy.Unsupported.PullRequest)!;
        var ex = refusal.ToException();
        ex.Message.Should().Be(refusal.Render(null));
        GitSyncRefusalException.Localize(ex, "de").Should().Be(refusal.Render("de"));
        GitSyncRefusalException.Localize(new InvalidOperationException("upstream"), "de").Should().Be("upstream");

        var entry = refusal.ToLogMessage(Microsoft.Extensions.Logging.LogLevel.Error);
        entry.MessageKey.Should().Be("gitsync.azure.unsupported.pullRequest");
        entry.Message.Should().Be(refusal.Render(null));
    }

    // ── Credential ───────────────────────────────────────────────────────────

    [Fact]
    public void TheAzureCredential_TravelsAsABearerHeaderInTheEnvironment_NeverInArgv()
    {
        const string token = "eyJ0eXAi.secret-entra-token";
        var (args, env) = GitCredentials.ForRemote(AzureUrl, token);

        args.Should().NotContain(a => a.Contains(token, StringComparison.Ordinal),
            "a token in argv is visible to every process listing on the host");
        env.Should().NotBeNull();
        env!["GIT_CONFIG_COUNT"].Should().Be("1");
        env["GIT_CONFIG_KEY_0"].Should().Be("http.extraHeader");
        env["GIT_CONFIG_VALUE_0"].Should().Be("Authorization: Bearer " + token);

        // Negative control: a GitHub remote keeps the existing credential helper, unchanged.
        var (ghArgs, ghEnv) = GitCredentials.ForRemote(GitHubUrl, token);
        ghArgs.Should().Equal(GitCredentials.AuthArgs(token));
        ghEnv!.ContainsKey("GW_TOKEN").Should().BeTrue();
        ghEnv.ContainsKey("GIT_CONFIG_COUNT").Should().BeFalse();
    }

    [Fact]
    public void TheDeclaredIdentity_BindsFromTheAzureDevOpsSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureDevOps:TenantId"] = "11111111-2222-3333-4444-555555555555",
                ["AzureDevOps:ClientId"] = "66666666-7777-8888-9999-000000000000",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddGitHubSyncServices();
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value;
        options.TenantId.Should().Be("11111111-2222-3333-4444-555555555555");
        options.ClientId.Should().Be("66666666-7777-8888-9999-000000000000");
        options.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task TheToken_IsMintedForTheDeclaredTenantAndTheAzureDevOpsScope()
    {
        using var pools = new IoPoolRegistry(new IoPoolOptions());
        var credential = new RecordingCredential("minted-token");
        AzureDevOpsOptions? seen = null;
        var service = new AzureReposTokenService(pools,
            Options.Create(new AzureDevOpsOptions { TenantId = "client-tenant", ClientId = "client-app" }),
            credentialFactory: declared => { seen = declared; return credential; },
            environment: WithProjectedToken);

        service.IsConfigured.Should().BeTrue();
        var token = await service.GetToken().Await(TestContext.Current.CancellationToken);

        token.Should().Be("minted-token");
        seen!.TenantId.Should().Be("client-tenant");
        seen.ClientId.Should().Be("client-app");
        credential.Requests.Should().ContainSingle();
        credential.Requests[0].Scopes.Should().Equal("499b84ac-1321-427f-aa17-267ca6975798/.default");
        credential.Requests[0].TenantId.Should().Be("client-tenant");
    }

    [Fact]
    public async Task AnUndeclaredIdentity_IsRefusedNamingTheKeys_AndBuildsNoCredential()
    {
        using var pools = new IoPoolRegistry(new IoPoolOptions());
        var built = false;
        var service = new AzureReposTokenService(pools,
            Options.Create(new AzureDevOpsOptions { TenantId = "client-tenant" }),   // ClientId missing
            credentialFactory: _ => { built = true; return new RecordingCredential("never"); });

        service.IsConfigured.Should().BeFalse();
        built.Should().BeFalse("no credential exists without both declared identifiers");
        var error = await service.GetToken().Materialize()
            .Where(n => n.Kind == System.Reactive.NotificationKind.OnError)
            .Select(n => n.Exception!)
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);
        error.Message.Should().Contain("AzureDevOps:TenantId").And.Contain("AzureDevOps:ClientId");
        ((GitSyncRefusalException)error).Refusal.Key.Should().Be("gitsync.azure.identityNotDeclared");
    }

    [Fact]
    public async Task ADeclaredIdentity_WithoutAProjectedWorkloadToken_IsRefusedByName_AndRequestsNoToken()
    {
        using var pools = new IoPoolRegistry(new IoPoolOptions());
        var credential = new RecordingCredential("never");
        var service = new AzureReposTokenService(pools,
            Options.Create(new AzureDevOpsOptions { TenantId = "client-tenant", ClientId = "client-app" }),
            credentialFactory: _ => credential,
            environment: _ => null);   // the pod was not armed by the workload-identity webhook

        var error = await service.GetToken().Materialize()
            .Where(n => n.Kind == System.Reactive.NotificationKind.OnError)
            .Select(n => n.Exception!)
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);
        ((GitSyncRefusalException)error).Refusal.Key.Should().Be("gitsync.azure.noWorkloadIdentity");
        error.Message.Should().Contain(AzureReposTokenService.FederatedTokenFileVariable);
        credential.Requests.Should().BeEmpty("no token exchange is attempted without a projected token");
    }

    private static string? WithProjectedToken(string name)
        => name == AzureReposTokenService.FederatedTokenFileVariable ? "/var/run/secrets/azure/tokens/azure-identity-token" : null;

    [Theory]
    [InlineData("https://dev.azure.com/client-a/Memex/_git/content", "ui.gitSync.provider.AzureRepos")]
    [InlineData("https://github.com/Systemorph/Content", "ui.gitSync.provider.GitHub")]
    [InlineData("/tmp/remote.git", "ui.gitSync.provider.Git")]
    public void TheSettingsTab_NamesTheProvider_WithAKeyBothLanguagesCarry(string url, string key)
    {
        using var pools = new IoPoolRegistry(new IoPoolOptions());
        var registry = new GitHostingProviders([new AzureReposGitHostingProvider(null, new AzureReposRepoClient(pools))]);
        GitHubSyncSettingsTab.ProviderKey(registry, url).Should().Be(key);
    }

    private sealed class RecordingCredential(string token) : TokenCredential
    {
        public ImmutableList<TokenRequestContext> Requests { get; private set; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Requests = Requests.Add(requestContext);
            return new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
