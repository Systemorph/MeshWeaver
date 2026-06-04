#pragma warning disable CS1591

using System;
using System.Threading.Tasks;
using MeshWeaver.AI.Copilot;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// On-demand REAL end-to-end test for co-hosted GitHub Copilot. Starts the local
/// Copilot CLI using the machine's logged-in user (no token read/stored by the
/// test) and asserts a real streamed response. Skipped unless <c>COPILOT_E2E=1</c>,
/// so it never runs in CI (no authenticated CLI) and never spends quota by accident.
///
/// <para>Run: <c>COPILOT_E2E=1 dotnet test test/MeshWeaver.AI.Test
/// --filter FullyQualifiedName~CopilotChatClientE2ETest</c> on a machine with an
/// authenticated <c>copilot</c> CLI.</para>
/// </summary>
public class CopilotChatClientE2ETest
{
    [Fact]
    public async Task RealCli_StreamsARealResponse()
    {
        if (Environment.GetEnvironmentVariable("COPILOT_E2E") != "1")
            Assert.Skip("Set COPILOT_E2E=1 to run the on-demand GitHub Copilot E2E (needs an authenticated `copilot` CLI; spends quota).");

        var config = new CopilotConfiguration
        {
            Models = ["auto"],
            SessionTimeoutMs = 120_000,
        };
        // No githubToken -> CopilotChatClient uses the machine's logged-in user
        // (UseLoggedInUser). "auto" lets Copilot self-select the model.
        await using var client = new CopilotChatClient(config, modelName: "auto");

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Reply with exactly the word: OK")],
            cancellationToken: TestContext.Current.CancellationToken);

        response.Text.Should().NotBeNullOrWhiteSpace();
    }
}
