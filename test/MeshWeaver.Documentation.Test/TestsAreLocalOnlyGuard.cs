using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard: unit and integration tests MUST run against LOCAL deployments only — local infra
/// (Testcontainers, in-memory) and LOCAL/fake LLMs (<c>FakeChatClient</c> / a local Ollama), NEVER a
/// cloud LLM API or a managed/remote database. This keeps the suite hermetic, deterministic, free, and
/// offline (and is why the suite is not flaky on network/cost grounds).
///
/// <para>The guard targets what actually makes a network call: constructing a REAL cloud-LLM SDK
/// client, or pointing at a managed/remote database. It deliberately does NOT flag cloud endpoint
/// STRINGS — those legitimately appear as provider-catalog config data (e.g. a catalog's
/// <c>DefaultEndpoint</c>) and in URL-construction tests that send through a FAKE handler, neither of
/// which calls out. Whitelist a genuine exception with a trailing <c>// local-only-guard:allow</c>.</para>
/// </summary>
public class TestsAreLocalOnlyGuard
{
    // Patterns that mean a test would actually TALK to a cloud LLM (a real provider SDK client) or a
    // managed/remote DB — none of which exist today (tests use FakeChatClient + Testcontainers). Not
    // endpoint strings (catalog config) or model names, which are fine. Catches a future regression.
    private static readonly string[] ForbiddenEndpoints =
    [
        // Real cloud-LLM SDK clients / registrations
        "new OpenAIClient(", "new AzureOpenAIClient(", "new AnthropicClient(", "new MistralClient(",
        "new CohereClient(", "new GenerativeModel(", "AddOpenAIChatClient", "AddAnthropicChatClient",
        // Managed / remote databases (tests use Testcontainers, never a hosted DB)
        ".postgres.database.azure.com", ".rds.amazonaws.com", ".database.windows.net",
    ];

    private const string AllowMarker = "local-only-guard:allow";

    [Fact]
    public void NoTestReachesACloudLlmOrRemoteDeployment()
    {
        var root = FindRepoRoot();
        var testDir = Path.Combine(root, "test");
        Assert.True(Directory.Exists(testDir), $"expected a 'test' directory at the repo root ({root})");

        var offenders = Directory
            .EnumerateFiles(testDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsUnderBinOrObj(f) && !IsThisGuard(f))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (file: f, line, no: i + 1)))
            .Where(x => !x.line.Contains(AllowMarker, StringComparison.Ordinal))
            .Where(x => ForbiddenEndpoints.Any(e => x.line.Contains(e, StringComparison.OrdinalIgnoreCase)))
            .Select(x => $"  {Path.GetRelativePath(root, x.file)}:{x.no}: {x.line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Tests must use LOCAL deployments + local/fake LLMs (Testcontainers, FakeChatClient, local Ollama) — "
            + "never a cloud LLM API or a remote/managed DB. Offending lines:\n" + string.Join("\n", offenders));
    }

    private static bool IsUnderBinOrObj(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static bool IsThisGuard(string path) =>
        Path.GetFileName(path).Equals("TestsAreLocalOnlyGuard.cs", StringComparison.Ordinal);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the repo root (MeshWeaver.slnx) from " + AppContext.BaseDirectory);
    }
}
