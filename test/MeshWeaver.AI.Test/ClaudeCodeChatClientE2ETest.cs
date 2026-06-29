#pragma warning disable CS1591

using System;
using System.Threading.Tasks;
using MeshWeaver.AI.ClaudeCode;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// On-demand REAL end-to-end test for co-hosted Claude Code. Spawns the actual
/// local <c>claude</c> CLI using its ambient <c>~/.claude</c> auth — no token is
/// read, injected, or stored by the test. Skipped unless <c>CLAUDE_CODE_E2E=1</c>
/// is set, so it never runs in CI (where there's no authenticated CLI) and never
/// spends quota by accident.
///
/// <para>Run it: <c>CLAUDE_CODE_E2E=1 dotnet test test/MeshWeaver.AI.Test
/// --filter FullyQualifiedName~ClaudeCodeChatClientE2ETest</c> on a machine with
/// an authenticated <c>claude</c> CLI.</para>
/// </summary>
public class ClaudeCodeChatClientE2ETest
{
    [Fact]
    public async Task RealCli_StreamsARealResponse()
    {
        if (Environment.GetEnvironmentVariable("CLAUDE_CODE_E2E") != "1")
            Assert.Skip("Set CLAUDE_CODE_E2E=1 to run the on-demand Claude Code E2E (needs an authenticated `claude` CLI; spends quota).");

        var config = new ClaudeCodeConfiguration
        {
            Models = ["sonnet"],
            SessionTimeoutMs = 120_000,
        };
        // Pass the user's auth THROUGH exactly as the co-hosted factory does:
        // CLAUDE_CODE_OAUTH_TOKEN from the env (your subscription token) is set on
        // the spawned CLI per request. If unset, oauthToken is null and the CLI
        // falls back to the machine's ambient ~/.claude login — still your auth.
        var token = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        var client = new ClaudeCodeChatClient(config, modelName: "sonnet", logger: null,
            configDir: null, oauthToken: token);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Reply with exactly the word: OK")],
            cancellationToken: TestContext.Current.CancellationToken);

        response.Text.Should().NotBeNullOrWhiteSpace();
    }
}
