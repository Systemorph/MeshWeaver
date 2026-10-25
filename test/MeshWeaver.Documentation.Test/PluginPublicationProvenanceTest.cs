using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using System.Text.Json.Nodes;
using MeshWeaver.Yaml;
using MeshWeaver.Fixture;

namespace MeshWeaver.Documentation.Test;

/// <summary>Executes the shared publisher's actual shell, replacing only Docker inspection and
/// the HTTP transport. No image, credential, registry or mesh is contacted.</summary>
public class PluginPublicationProvenanceTest
{
    private const string Workflow = ".github/workflows/node-repo-publish-bake.yml";
    private const string ContentSha = "2222222222222222222222222222222222222222";
    private const string WorkflowSha = "1111111111111111111111111111111111111111";
    private const string Version = "3.0.0-ci.8329";

    [Theory]
    [InlineData("Systemorph/MeshWeaver")]
    [InlineData("Systemorph/MeshWeaver.Plugins")]
    public void PublicationNamesTheBuiltContent_NotTheWorkflowCheckout(string workflowRepository)
    {
        using var run = Publish(ContentSha, Version, workflowRepository);
        using var json = JsonDocument.Parse(File.ReadAllText(run.File("body")));
        var body = json.RootElement;
        Assert.Equal("bundle-publication", body.GetProperty("event").GetString());
        Assert.Equal("Systemorph/MeshWeaver.Plugins", body.GetProperty("repo").GetString());
        Assert.Equal(ContentSha, body.GetProperty("sha").GetString());
        Assert.Equal(Version, body.GetProperty("version").GetString());
        Assert.True(run.Exit == 0, run.Output);
        Assert.Equal("plugins", body.GetProperty("source").GetString());
        Assert.Equal("sfixture", body.GetProperty("identity").GetString());
        Assert.Equal("sha256:tester", body.GetProperty("digest").GetString());
        Assert.Equal("registry.invalid/portal@sha256:portal", body.GetProperty("platformImage").GetString());
        Assert.Equal($"https://github.com/{workflowRepository}/actions/runs/123", body.GetProperty("run").GetString());
        Assert.Equal(new[] { "crm", "education" }, body.GetProperty("upstreams").EnumerateArray().Select(x => x.GetString()));
        Assert.Single(File.ReadAllLines(run.File("requests")));
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes("synthetic-not-a-credential"),
            File.ReadAllBytes(run.File("body")));
        Assert.Equal("sha256=" + Convert.ToHexStringLower(signature), File.ReadAllText(run.File("signature")));
    }

    [Theory]
    [InlineData("3.0.0")]
    [InlineData("3.0.0-rc9.ci.7824")]
    [InlineData("3.0.0-edge.7977")]
    [InlineData("3.0.0-rc1.edge.5")]
    public void EveryShapeThePipelineMintsIsAnnounced(string version)
    {
        using var run = Publish(ContentSha, version, "Systemorph/MeshWeaver");
        Assert.True(run.Exit == 0, run.Output);
        using var json = JsonDocument.Parse(File.ReadAllText(run.File("body")));
        Assert.Equal(version, json.RootElement.GetProperty("version").GetString());
    }

    [Theory]
    [InlineData("", Version)]
    [InlineData("main", Version)]
    [InlineData(WorkflowSha + "0", Version)]
    [InlineData(ContentSha + "\n", Version)]
    [InlineData(ContentSha, "")]
    [InlineData(ContentSha, "latest")]
    [InlineData(ContentSha, "3.0.0-alpha")]
    [InlineData(ContentSha, "3.0.0-preview.1")]
    [InlineData(ContentSha, "3.0.0-rc9")]
    [InlineData(ContentSha, "3.0.0-ci.8329\ninjected=value")]
    [InlineData(ContentSha, "3.0.0/other")]
    public void InvalidProducerOutputsCannotPost(string sha, string version)
    {
        using var run = Publish(sha, version, "Systemorph/MeshWeaver");
        Assert.True(run.Exit != 0, run.Output);
        Assert.Contains("::error::", run.Output);
        Assert.False(File.Exists(run.File("requests")), "Invalid provenance must fail before HTTP, not after a misleading record lands.");
    }

    [Theory]
    [InlineData("[\"OTHER=value\",\"MESHWEAVER_PLATFORM_VERSION=3.0.0-ci.8329\"]", "3.0.0-ci.8329")]
    [InlineData("[\"MESHWEAVER_PLATFORM_VERSION=3.0.0\"]", "3.0.0")]
    [InlineData("[\"MESHWEAVER_PLATFORM_VERSION=3.0.0-rc9.ci.7824\"]", "3.0.0-rc9.ci.7824")]
    [InlineData("[\"MESHWEAVER_PLATFORM_VERSION=3.0.0-edge.7977\"]", "3.0.0-edge.7977")]
    [InlineData("[]", null)]
    [InlineData("null", null)]
    [InlineData("not-json", null)]
    [InlineData("[\"MESHWEAVER_PLATFORM_VERSION=\"]", null)]
    [InlineData("[\"MESHWEAVER_PLATFORM_VERSION=latest\"]", null)]
    [InlineData("[\"MESHWEAVER_PLATFORM_VERSION=3.0.0-alpha\"]", null)]
    [InlineData("[\"MESHWEAVER_PLATFORM_VERSION=3.0.0-preview.1\"]", null)]
    [InlineData("[\"MESHWEAVER_PLATFORM_VERSION=3.0.0-rc9\"]", null)]
    [InlineData("[\"MESHWEAVER_PLATFORM_VERSION=3.0.0\",\"MESHWEAVER_PLATFORM_VERSION=3.0.1\"]", null)]
    public void ReleaseVersionComesFromTheSelectedPortalConfig(string config, string? expected)
    {
        using var run = new ShellRun(ImmutableDictionary<string, string>.Empty
            .Add("PORTAL_REF", "registry.invalid/portal@sha256:portal")
            .Add("IMAGE_CONFIG", config));
        run.Execute(Step("publish-bake", "id", "platform-version"), """
            docker() {
              printf '%s\n' "$*" >> "$FIXTURE/docker-requests"
              printf '%s' "$IMAGE_CONFIG"
            }
            """);
        if (expected is not null)
        {
            Assert.True(run.Exit == 0, run.Output);
            Assert.Equal($"version={expected}\n", File.ReadAllText(run.File("outputs")));
        }
        else
        {
            Assert.True(run.Exit != 0, run.Output);
            Assert.False(File.Exists(run.File("outputs")));
        }
        Assert.Equal("image inspect registry.invalid/portal@sha256:portal --format {{json .Config.Env}}\n",
            File.ReadAllText(run.File("docker-requests")));
    }

    [Fact]
    public void AnUnreadableSelectedImageCannotInventAReleaseVersion()
    {
        using var run = new ShellRun(ImmutableDictionary<string, string>.Empty
            .Add("PORTAL_REF", "registry.invalid/portal@sha256:portal"));
        run.Execute(Step("publish-bake", "id", "platform-version"), "docker() { return 9; }");
        Assert.True(run.Exit != 0, run.Output);
        Assert.False(File.Exists(run.File("outputs")));
    }

    [Fact]
    public void ProducerOutputsReachTheFinalJobWithoutEventMetadataFallback()
    {
        var jobs = Jobs();
        var bake = (JsonObject)jobs["publish-bake"]!;
        var outputs = (JsonObject)bake["outputs"]!;
        Assert.Equal("${{ steps.content.outputs.sha }}", TolerantYaml.ScalarText(outputs["content-sha"]));
        Assert.Equal("${{ steps.platform-version.outputs.version }}", TolerantYaml.ScalarText(outputs["version"]));
        var post = FindStep("register-publication", "name", "Sign and POST the publication record");
        var env = (JsonObject)post["env"]!;
        Assert.Equal("${{ needs.publish-bake.outputs.content-sha }}", TolerantYaml.ScalarText(env["CONTENT_SHA"]));
        Assert.Equal("${{ needs.publish-bake.outputs.version }}", TolerantYaml.ScalarText(env["RELEASED_VERSION"]));
    }

    [Fact]
    public void AFleetRegistryPushWithoutItsIndexDigestIsRefusedBeforeTheRecordIsWritten()
    {
        // The case the assertion exists for: the lane pushed to the fleet registry (a registry is
        // configured) but the push step's `index_digest` output never reached this job. The record
        // must not be written at all — not posted, not even composed.
        using var run = Publish(ContentSha, Version, "Systemorph/MeshWeaver",
            ImmutableDictionary<string, string>.Empty.Add("BUNDLE_REGISTRY", "cr.invalid"));
        Assert.True(run.Exit != 0, run.Output);
        Assert.Contains("::error::publish-bake reported no fleet-registry index digest", run.Output);
        Assert.False(File.Exists(run.File("body")), "A publication the registry cannot be asked about must never be recorded.");
        Assert.False(File.Exists(run.File("requests")), "The refusal must precede HTTP, not follow a misleading record.");
    }

    [Fact]
    public void AFleetRegistryPushWithItsIndexDigestIsRecordedAndNamed()
    {
        using var run = Publish(ContentSha, Version, "Systemorph/MeshWeaver",
            ImmutableDictionary<string, string>.Empty
                .Add("BUNDLE_REGISTRY", "cr.invalid")
                .Add("INDEX_DIGEST", "sha256:index"));
        Assert.True(run.Exit == 0, run.Output);
        Assert.Contains("fleet registry: cr.invalid/plugins/plugins@sha256:index", run.Output);
        Assert.Single(File.ReadAllLines(run.File("requests")));
        Assert.Contains("cr.invalid/plugins/plugins@sha256:index", File.ReadAllText(run.File("summary")));
    }

    private static ShellRun Publish(string sha, string version, string workflowRepository,
        ImmutableDictionary<string, string>? registry = null)
    {
        var run = new ShellRun((registry ?? ImmutableDictionary<string, string>.Empty)
            .Add("URL", "https://inbox.invalid/api/hooks/Hosting/PlatformBuilds")
            .Add("SECRET", "synthetic-not-a-credential")
            .Add("SOURCE", "plugins")
            .Add("SELF", "Systemorph/MeshWeaver.Plugins")
            .Add("GITHUB_REPOSITORY", workflowRepository)
            .Add("GITHUB_SHA", WorkflowSha)
            .Add("GITHUB_RUN_ID", "123")
            .Add("GITHUB_SERVER_URL", "https://github.com")
            .Add("CONTENT_SHA", sha)
            .Add("RELEASED_VERSION", version)
            .Add("IMAGE", "registry.invalid/tester@sha256:tester")
            .Add("PLATFORM_IMAGE", "registry.invalid/portal@sha256:portal")
            .Add("IDENTITY", "sfixture")
            .Add("UPSTREAMS", "CRM,education;crm"));
        run.Execute(Step("register-publication", "name", "Sign and POST the publication record"), """
            curl() {
              printf 'POST\n' >> "$FIXTURE/requests"
              while [ "$#" -gt 0 ]; do
                case "$1" in
                  --data) printf '%s' "$2" > "$FIXTURE/body"; shift ;;
                  -H) case "$2" in X-Hub-Signature-256:*) printf '%s' "${2#*: }" > "$FIXTURE/signature" ;; esac; shift ;;
                esac
                shift
              done
              printf '{"signature":"verified"}' > "$RESP"
              printf '200'
            }
            """);
        return run;
    }

    private static JsonObject Jobs() =>
        (JsonObject)TolerantYaml.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, Workflow)))!["jobs"]!;

    private static JsonObject FindStep(string job, string key, string value) =>
        ((JsonArray)Jobs()[job]!["steps"]!).Cast<JsonObject>()
        .Single(step => step.TryGetPropertyValue(key, out var found) && TolerantYaml.ScalarText(found) == value);

    private static string Step(string job, string key, string value) =>
        TolerantYaml.ScalarText(FindStep(job, key, value)["run"])!;

    private sealed class ShellRun : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "mw-publication-" + Guid.NewGuid().ToString("N"));
        private readonly ImmutableDictionary<string, string> environment;
        public int Exit { get; private set; }
        public string Output { get; private set; } = "";
        public string File(string name) => Path.Combine(directory, name);

        public ShellRun(ImmutableDictionary<string, string> stepEnvironment)
        {
            Directory.CreateDirectory(directory);
            environment = stepEnvironment
                .SetItem("FIXTURE", directory)
                .SetItem("RESP", File("response"))
                .SetItem("GITHUB_OUTPUT", File("outputs"))
                .SetItem("GITHUB_STEP_SUMMARY", File("summary"));
        }

        public void Execute(string script, string transport)
        {
            System.IO.File.WriteAllText(File("step.sh"), transport + "\n" + script);
            var start = new ProcessStartInfo("bash")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory,
            };
            start.ArgumentList.Add(File("step.sh"));
            foreach (var (key, value) in environment)
                start.Environment[key] = value;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("bash did not start");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Exit = process.ExitCode;
            Output = stdout + stderr;
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
