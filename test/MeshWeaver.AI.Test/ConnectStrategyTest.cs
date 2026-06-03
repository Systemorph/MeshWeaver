#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Models;
using MeshWeaver.AI;
using MeshWeaver.AI.Connect;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Drives the per-user CLI Connect backend (<see cref="ClaudeConnectStrategy"/> +
/// <see cref="ConnectSessionManager"/>) against a committed, deterministic fake CLI
/// (test/MeshWeaver.AI.Test.FakeCli) — no real <c>claude</c>, no quota, no TTY. Proves:
/// IsLoggedIn false → connect → token captured → a <c>ModelProvider</c> node is written with an
/// <c>enc:</c>-tagged key that round-trips through <see cref="ChatClientCredentialResolver"/>; and
/// that login-status reports connected when the fake CLI has "logged in".
///
/// <para>The real-CLI E2E is a separate path gated behind <c>CLAUDE_CONNECT_E2E=1</c> (see the
/// <c>RealClaudeSetupToken_*</c> facts).</para>
/// </summary>
public class ConnectStrategyTest : AITestBase
{
    public ConnectStrategyTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => false;

    // Master key on so the stored ApiKey is enc:-tagged ciphertext (round-trip assertion).
    private readonly IConfiguration encryptionConfig = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:KeyProtection:MasterKey"] = "test-master-key-connect-roundtrip-do-not-use",
        })
        .Build();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // ClaudeCode catalog source (Kind = Cli) so the provider resolves like the portal.
            .AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
                SectionName: "ClaudeCode",
                ProviderName: "ClaudeCode",
                Order: 5,
                DisplayLabel: "Claude Code (my subscription)",
                DefaultEndpoint: null,
                DefaultModelIds: ImmutableArray.Create("sonnet", "opus", "haiku"),
                RequiresApiKey: true,
                Kind: ProviderKind.Cli))
            .ConfigureServices(services =>
            {
                services.AddSingleton<IConfiguration>(encryptionConfig);
                services.AddSingleton<ModelProviderService>();
                services.AddSingleton<IConnectTokenSink, ConnectTokenSink>();
                services.AddSingleton<IConnectStrategy, ClaudeConnectStrategy>();
                services.AddSingleton<ConnectSessionManager>();
                // Point the Claude strategy at the committed fake CLI: spawn `dotnet <FakeCli.dll>`.
                services.Configure<ClaudeConnectOptions>(o =>
                {
                    o.FileName = "dotnet";
                    o.Arguments = new[] { FakeCliDllPath() };
                    o.UrlTimeout = TimeSpan.FromSeconds(20);
                    o.TokenTimeout = TimeSpan.FromSeconds(20);
                });
                return services;
            });

    private ConnectSessionManager Manager => Mesh.ServiceProvider.GetRequiredService<ConnectSessionManager>();

    /// <summary>Locate the fake-CLI assembly copied next to the test output under FakeCli/.</summary>
    private static string FakeCliDllPath()
    {
        var dir = AppContext.BaseDirectory;
        var path = Path.Combine(dir, "FakeCli", "MeshWeaver.AI.Test.FakeCli.dll");
        if (File.Exists(path)) return path;
        // Fallback: walk up to the repo and find the FakeCli build output (any config).
        var found = Directory.GetFiles(dir, "MeshWeaver.AI.Test.FakeCli.dll", SearchOption.AllDirectories).FirstOrDefault();
        return found ?? path;
    }

    // 🚨 Reactive assertions ONLY — this test MUST stay a `void` method with NO
    // `await` / `.ToTask()` / `.GetAwaiter().GetResult()`. SubmitCode's connect chain
    // completes INLINE on the mesh hub's action block (the last child-model
    // CreateNodeResponse triggers CombineLatest → Connected synchronously there). An
    // `await ...ToTask()` would resume the continuation ON that action-block thread, and
    // the subsequent BLOCKING reads (GetMeshNodeStream, .Match) would then deadlock against
    // the action block needing to serve those very reads — the 8s + 15s = 23s stall this
    // test used to hit. A `void` method's reactive assertions block the TEST thread, never
    // the action block, so the chain completes freely. See ReactiveTestAssertions.md.
    [Fact]
    public void PasteCodeFlow_CapturesToken_WritesEncryptedModelProvider_RoundTrips()
    {
        var owner = $"user-{Guid.NewGuid():N}";
        var configDir = Path.Combine(Path.GetTempPath(), $"connect-test-{Guid.NewGuid():N}", ".claude");

        // Establish the owner's partition root first. The connect flow writes the credential
        // node at {owner}/_Provider/ClaudeCode; in production the owner is the logged-in user
        // whose partition already exists.
        NodeFactory.CreateNode(new MeshNode(owner) { Name = owner, NodeType = "Markdown" })
            .Should().Within(10.Seconds()).Emit();

        // 1. Not logged in initially (no credentials file).
        var loggedInBefore = Manager.IsLoggedIn(ConnectProvider.ClaudeCode, configDir)
            .Should().Within(10.Seconds()).Emit();
        loggedInBefore.Should().BeFalse();

        // 2. Connect → the fake CLI prints an auth URL → Connecting state with the URL.
        var connecting = Manager.StartConnect(owner, ConnectProvider.ClaudeCode, configDir)
            .Should().Within(10.Seconds()).Emit();
        var challenge = connecting.Should().BeOfType<ConnectStatus.Connecting>().Which.Challenge;
        challenge.RequiresPastedCode.Should().BeTrue();
        challenge.VerificationUrl.Should().StartWith("https://");

        // 3. Submit the code → the fake CLI prints the token → Connected, provider node written.
        var connected = Manager.SubmitCode(owner, ConnectProvider.ClaudeCode, "the-pasted-code")
            .Should().Within(20.Seconds()).Emit();
        Output.WriteLine($"[diag] connect status = {connected.GetType().Name}: {connected}");
        var ok = connected.Should().BeOfType<ConnectStatus.Connected>().Which;
        Output.WriteLine($"[diag] connected path={ok.ProviderNodePath} fp={ok.KeyFingerprint}");
        ok.ProviderNodePath.Should().Be($"{owner}/_Provider/ClaudeCode");
        ok.KeyFingerprint.Should().NotBe("(empty)");

        // Diagnostic single-node read — confirms the satellite read path resolves.
        var directRead = Mesh.GetWorkspace().GetMeshNodeStream(ok.ProviderNodePath)
            .Take(1).Timeout(8.Seconds())
            .Catch((Exception ex) => { Output.WriteLine($"[diag] direct read threw: {ex.Message}"); return Observable.Return<MeshNode?>(null); })
            .Should().Within(10.Seconds()).Emit();
        Output.WriteLine($"[diag] direct read node null={directRead is null} content={(directRead?.Content?.GetType().Name ?? "(none)")}");

        // 4. AT REST: the stored ApiKey is enc:-tagged ciphertext, not the captured token.
        var workspace = Mesh.GetWorkspace();
        var providerService = Mesh.ServiceProvider.GetRequiredService<ModelProviderService>();
        // Pre-warm the synced query so the owner partition is active before the per-node read
        // (mirrors ModelProviderServiceTest.RotateKey).
        var listed = providerService.GetProvidersForOwner(owner)
            .Should().Within(15.Seconds()).Match(p => p.Count > 0);
        Output.WriteLine($"[diag] providers listed={listed.Count} key fp={listed[0].ApiKeyFingerprint}");
        var node = workspace.GetMeshNodeStream(ok.ProviderNodePath)
            .Should().Within(15.Seconds())
            .Match(n => (n.Content as ModelProviderConfiguration)?.ApiKey is { Length: > 0 });
        var storedKey = ((ModelProviderConfiguration)node.Content!).ApiKey!;
        Output.WriteLine($"[diag] stored ApiKey prefix={storedKey[..Math.Min(12, storedKey.Length)]}");
        storedKey.Should().StartWith("enc:v1:", "encryption-at-rest must tag the stored key");
        storedKey.Should().NotContain("FAKE-TOKEN", "the literal token must never be stored in plaintext");

        // 5. ON READ: the resolver decrypts the stored ciphertext back to the captured token.
        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.EnsureSubscription();
        resolver.WatchPartition(owner);
        var resolution = Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => resolver.Resolve("sonnet"))
            .Should().Within(15.Seconds()).Match(r => r.ApiKey != null);
        resolution.ApiKey.Should().StartWith("sk-ant-FAKE-TOKEN",
            "the resolver decrypts the ModelProvider key the Connect flow stored");
    }

    [Fact]
    public async Task IsLoggedIn_True_WhenCredentialsFilePresent()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var configDir = Path.Combine(Path.GetTempPath(), $"connect-loggedin-{Guid.NewGuid():N}", ".claude");
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(
            Path.Combine(configDir, ".credentials.json"),
            "{\"accessToken\":\"sk-ant-EXISTING-LOGIN-1234\"}", ct);

        var loggedIn = await Manager.IsLoggedIn(ConnectProvider.ClaudeCode, configDir).FirstAsync().ToTask(ct);
        loggedIn.Should().BeTrue();
    }

    [Fact]
    public async Task IsLoggedIn_False_WhenNoConfigDir()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var loggedIn = await Manager.IsLoggedIn(ConnectProvider.ClaudeCode, userConfigDir: null).FirstAsync().ToTask(ct);
        loggedIn.Should().BeFalse();
    }

    [Fact]
    public async Task RealClaudeSetupToken_Gated_BehindEnvFlag()
    {
        if (Environment.GetEnvironmentVariable("CLAUDE_CONNECT_E2E") != "1")
            Assert.Skip("Set CLAUDE_CONNECT_E2E=1 to run the on-demand real `claude setup-token` Connect E2E " +
                        "(needs a PTY + an interactive browser login; see TODO(claude-pty) in ClaudeConnectStrategy).");

        // Real CLI path — construct a strategy with DEFAULT options (real `claude setup-token`),
        // bypassing the fake-CLI configured on the mesh. NOTE: claude setup-token is TTY-gated
        // (probed 2026-06-01) — without a PTY wrapper this times out waiting for the URL. Left as
        // the developer-run E2E hook.
        var ct = new CancellationTokenSource(2.Minutes()).Token;
        var owner = $"user-{Guid.NewGuid():N}";
        var configDir = Path.Combine(Path.GetTempPath(), $"connect-real-{Guid.NewGuid():N}", ".claude");

        var realServices = new ServiceCollection().BuildServiceProvider();
        var strategy = new ClaudeConnectStrategy(realServices);
        var session = new ConnectSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            OwnerPath = owner,
            Provider = ConnectProvider.ClaudeCode,
            ConfigDir = configDir,
        };
        var challenge = await strategy.StartConnect(session, owner).FirstAsync().ToTask(ct);
        challenge.VerificationUrl.Should().StartWith("https://");
    }
}
