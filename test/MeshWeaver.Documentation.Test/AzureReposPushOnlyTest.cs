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

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 Policy <c>azure-repos-push-only</c> (MeshWeaver#5248), as data: an Azure Repos URL is
/// recognised as such, an Azure Repos source syncs mesh → repo only and every inbound operation
/// on it is refused BY NAME, and the push authenticates with the Azure DevOps identity DECLARED in
/// the instance's configuration (its deployment record) — never a stored secret. Every rule has a
/// GitHub negative control beside it, so a predicate that answered "Azure" for everything (or
/// nothing) goes red.
/// </summary>
public class AzureReposPushOnlyTest
{
    // ── URL recognition ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://dev.azure.com/partnerre/Memex/_git/content", "partnerre", "Memex", "content")]
    [InlineData("https://partnerre@dev.azure.com/partnerre/Memex/_git/content", "partnerre", "Memex", "content")]
    [InlineData("https://dev.azure.com/partnerre/My%20Project/_git/content.git", "partnerre", "My Project", "content")]
    [InlineData("https://partnerre.visualstudio.com/Memex/_git/content", "partnerre", "Memex", "content")]
    [InlineData("https://partnerre.visualstudio.com/DefaultCollection/Memex/_git/content", "partnerre", "Memex", "content")]
    public void AnAzureReposUrl_IsRecognised(string url, string org, string project, string repo)
    {
        AzureReposRepository.TryParse(url, out var parsed).Should().BeTrue();
        parsed.Should().Be(new AzureReposRepository(org, project, repo));
        GitRepositoryProvider.Classify(url).Should().Be(GitRepositoryProvider.AzureRepos);
    }

    [Theory]
    [InlineData("https://github.com/Systemorph/MeshWeaver", "GitHub")]
    [InlineData("https://github.com/Systemorph/MeshWeaver.git", "GitHub")]
    [InlineData("https://dev.azure.com/partnerre/Memex", "Git")]                 // no _git segment
    [InlineData("https://dev.azure.com/partnerre/Memex/_git/content/extra", "Git")] // too deep
    [InlineData("http://dev.azure.com/partnerre/Memex/_git/content", "Git")]     // not https
    [InlineData("https://gitlab.example.com/a/b", "Git")]
    [InlineData("/tmp/local-remote.git", "Git")]
    [InlineData("", "Git")]
    public void EveryOtherUrl_IsNotAzureRepos(string url, string expected)
    {
        AzureReposRepository.TryParse(url, out _).Should().BeFalse();
        GitRepositoryProvider.Classify(url).Should().Be(expected);
    }

    // ── Push-only ────────────────────────────────────────────────────────────

    private const string AzureUrl = "https://dev.azure.com/partnerre/Memex/_git/content";
    private const string GitHubUrl = "https://github.com/Systemorph/Content";

    [Theory]
    [InlineData(SyncDirection.Bidirectional)]
    [InlineData(SyncDirection.ImportOnly)]
    public void AnAzureReposExport_NotDeclaredExportOnly_IsRefusedByName(SyncDirection direction)
    {
        var refusal = AzureReposPushPolicy.RefuseExport(
            new GitHubSyncConfig { RepositoryUrl = AzureUrl, Direction = direction });
        refusal.Should().NotBeNull();
        refusal!.Key.Should().Be("gitsync.azure.exportNotExportOnly");
        refusal.Render(null).Should().Contain("push-only").And.Contain("Export-only").And.Contain(direction.ToString());
    }

    [Fact]
    public void AnAzureReposExport_DeclaredExportOnly_Runs()
        => AzureReposPushPolicy.RefuseExport(
                new GitHubSyncConfig { RepositoryUrl = AzureUrl, Direction = SyncDirection.ExportOnly })
            .Should().BeNull();

    [Theory]
    [InlineData(SyncDirection.Bidirectional)]
    [InlineData(SyncDirection.ImportOnly)]
    [InlineData(SyncDirection.ExportOnly)]
    public void AGitHubExport_IsNeverJudgedByThisPolicy(SyncDirection direction)
        => AzureReposPushPolicy.RefuseExport(
                new GitHubSyncConfig { RepositoryUrl = GitHubUrl, Direction = direction })
            .Should().BeNull("the negative control: the push-only rule must not touch GitHub sources");

    [Theory]
    [InlineData(AzureReposPushPolicy.InboundOperation.Reimport, "Re-import")]
    [InlineData(AzureReposPushPolicy.InboundOperation.Import, "Import")]
    [InlineData(AzureReposPushPolicy.InboundOperation.CheckBranch, "Check branch")]
    [InlineData(AzureReposPushPolicy.InboundOperation.BranchHead, "Branch head lookup")]
    [InlineData(AzureReposPushPolicy.InboundOperation.PullRequest, "Pull requests")]
    public void EveryInboundOperation_OnAzureRepos_IsRefusedByName(string operation, string englishLead)
    {
        var refusal = AzureReposPushPolicy.RefuseInbound(AzureUrl, operation);
        refusal.Should().NotBeNull();
        refusal!.Render(null).Should().StartWith(englishLead).And.Contain("push-only").And.Contain(AzureUrl);
        AzureReposPushPolicy.RefuseInbound(GitHubUrl, operation).Should().BeNull(
            "the negative control: inbound GitHub operations are untouched");
    }

    // ── Guard ORDER — the sequences GitHubSyncService runs (review #6377) ─────

    [Theory]
    [InlineData(SyncDirection.ImportOnly)]
    [InlineData(SyncDirection.Bidirectional)]
    public void TheExportGuard_RunsTheAzureRuleBeforeTheDirectionRule(SyncDirection direction)
    {
        // An Azure source declared ImportOnly matches BOTH rules; the provider's refusal must win,
        // or the user is told "change to Bidirectional" — which Azure would refuse next.
        var refusal = AzureReposPushPolicy.ExportGuard(
            new GitHubSyncConfig { RepositoryUrl = AzureUrl, Direction = direction });
        refusal!.Key.Should().Be("gitsync.azure.exportNotExportOnly");
    }

    [Fact]
    public void TheExportGuard_StillAppliesTheDirectionRuleToGitHub()
    {
        // Negative control: the generic rule is not lost behind the provider rule.
        AzureReposPushPolicy.ExportGuard(
                new GitHubSyncConfig { RepositoryUrl = GitHubUrl, Direction = SyncDirection.ImportOnly })!
            .Key.Should().Be("gitsync.direction.importOnlyNoExport");
        AzureReposPushPolicy.ExportGuard(
                new GitHubSyncConfig { RepositoryUrl = GitHubUrl, Direction = SyncDirection.Bidirectional })
            .Should().BeNull();
        AzureReposPushPolicy.ExportGuard(
                new GitHubSyncConfig { RepositoryUrl = AzureUrl, Direction = SyncDirection.ExportOnly })
            .Should().BeNull("a correctly declared Azure source exports");
    }

    [Theory]
    [InlineData(SyncDirection.ExportOnly)]
    [InlineData(SyncDirection.Bidirectional)]
    [InlineData(SyncDirection.ImportOnly)]
    public void TheReimportGuard_RunsTheAzureRuleBeforeTheDirectionRule(SyncDirection direction)
    {
        // An Azure source correctly declared ExportOnly matches BOTH rules on re-import; the
        // provider's refusal must win, or the user is told to switch to Import-only.
        AzureReposPushPolicy.ReimportGuard(
                new GitHubSyncConfig { RepositoryUrl = AzureUrl, Direction = direction })!
            .Key.Should().Be("gitsync.azure.inbound.reimport");
    }

    [Fact]
    public void TheReimportGuard_StillAppliesTheDirectionRuleToGitHub()
    {
        AzureReposPushPolicy.ReimportGuard(
                new GitHubSyncConfig { RepositoryUrl = GitHubUrl, Direction = SyncDirection.ExportOnly })!
            .Key.Should().Be("gitsync.direction.exportOnlyNoImport");
        AzureReposPushPolicy.ReimportGuard(
                new GitHubSyncConfig { RepositoryUrl = GitHubUrl, Direction = SyncDirection.Bidirectional })
            .Should().BeNull();
    }

    [Fact]
    public void TheService_RunsTheGuardSequences_NotItsOwnChecks()
    {
        // The sequences above are only the service's behaviour if the service calls THEM. Pin it on
        // the source: SyncToGitHub and the re-import call the guards and carry no direction check of
        // their own that could run first.
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(
            RepoRoot(), "src", "MeshWeaver.GitSync", "GitHubSyncService.cs"));
        source.Should().Contain("AzureReposPushPolicy.ExportGuard(config)");
        source.Should().Contain("AzureReposPushPolicy.ReimportGuard(config)");
        source.Contains("config.Direction == SyncDirection.ImportOnly", StringComparison.Ordinal)
            .Should().BeFalse("an inline import-only check in the service would bypass the guard order");
        source.Contains("if (config.Direction == SyncDirection.ExportOnly)", StringComparison.Ordinal)
            .Should().BeFalse("an inline export-only check in the service would bypass the guard order");
    }

    [Fact]
    public void TheGenericDirectionRefusals_KeepTheirShippedExceptionTypeAndMessage()
    {
        // The GitHub path must behave exactly as before this change: MeshWeaver.Plugins'
        // SyncDirectionAndSourcesTest asserts Assert.Throws<InvalidOperationException> (an EXACT
        // type match) and the English text. A GitSyncRefusalException here broke it.
        var export = AzureReposPushPolicy.ExportGuard(
            new GitHubSyncConfig { RepositoryUrl = GitHubUrl, Direction = SyncDirection.ImportOnly })!.ToException();
        export.GetType().Should().Be(typeof(InvalidOperationException));
        export.Message.Should().Be(
            $"This sync source is import-only (repo → mesh): exporting to {GitHubUrl} is not allowed. " +
            "Change the source's Sync direction to Bidirectional or Export-only to commit.");

        var import = AzureReposPushPolicy.ReimportGuard(
            new GitHubSyncConfig { RepositoryUrl = GitHubUrl, Direction = SyncDirection.ExportOnly })!.ToException();
        import.GetType().Should().Be(typeof(InvalidOperationException));
        import.Message.Should().Be(
            $"This sync source is export-only (mesh → repo): importing from {GitHubUrl} is not allowed. " +
            "Change the source's Sync direction to Bidirectional or Import-only to re-import.");

        // Negative control: an Azure Repos refusal IS the localizable type.
        AzureReposPushPolicy.ExportGuard(
                new GitHubSyncConfig { RepositoryUrl = AzureUrl, Direction = SyncDirection.ImportOnly })!
            .ToException().GetType().Should().Be(typeof(GitSyncRefusalException));
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root (MeshWeaver.slnx) not found");
    }

    // ── Localization of every refusal ────────────────────────────────────────

    [Theory]
    [InlineData("gitsync.azure.exportNotExportOnly")]
    [InlineData("gitsync.azure.inbound.import")]
    [InlineData("gitsync.azure.inbound.reimport")]
    [InlineData("gitsync.azure.inbound.checkBranch")]
    [InlineData("gitsync.azure.inbound.branchHead")]
    [InlineData("gitsync.azure.inbound.pullRequest")]
    [InlineData("gitsync.azure.identityNotDeclared")]
    [InlineData("gitsync.azure.noWorkloadIdentity")]
    [InlineData("gitsync.azure.notRegistered")]
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
        var refusal = AzureReposPushPolicy.RefuseInbound(AzureUrl, AzureReposPushPolicy.InboundOperation.Reimport)!;
        var ex = refusal.ToException();
        ex.Message.Should().Be(refusal.Render(null));
        GitSyncRefusalException.Localize(ex, "de").Should().Be(refusal.Render("de"));
        GitSyncRefusalException.Localize(new InvalidOperationException("upstream"), "de").Should().Be("upstream");

        var entry = refusal.ToLogMessage(Microsoft.Extensions.Logging.LogLevel.Error);
        entry.MessageKey.Should().Be("gitsync.azure.inbound.reimport");
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
    [InlineData("https://dev.azure.com/partnerre/Memex/_git/content", "ui.gitSync.provider.AzureRepos")]
    [InlineData("https://github.com/Systemorph/Content", "ui.gitSync.provider.GitHub")]
    [InlineData("/tmp/remote.git", "ui.gitSync.provider.Git")]
    public void TheSettingsTab_NamesTheProvider_WithAKeyBothLanguagesCarry(string url, string key)
    {
        GitHubSyncSettingsTab.ProviderKey(url).Should().Be(key);
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
