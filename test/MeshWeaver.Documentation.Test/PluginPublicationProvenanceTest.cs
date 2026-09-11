using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using YamlDotNet.RepresentationModel;

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
        var bake = (YamlMappingNode)jobs.Children[new YamlScalarNode("publish-bake")];
        var outputs = (YamlMappingNode)bake.Children[new YamlScalarNode("outputs")];
        Assert.Equal("${{ steps.content.outputs.sha }}", outputs.Children[new YamlScalarNode("content-sha")].ToString());
        Assert.Equal("${{ steps.platform-version.outputs.version }}", outputs.Children[new YamlScalarNode("version")].ToString());
        var post = FindStep("register-publication", "name", "Sign and POST the publication record");
        var env = (YamlMappingNode)post.Children[new YamlScalarNode("env")];
        Assert.Equal("${{ needs.publish-bake.outputs.content-sha }}", env.Children[new YamlScalarNode("CONTENT_SHA")].ToString());
        Assert.Equal("${{ needs.publish-bake.outputs.version }}", env.Children[new YamlScalarNode("RELEASED_VERSION")].ToString());
    }

    private static ShellRun Publish(string sha, string version, string workflowRepository)
    {
        var run = new ShellRun(ImmutableDictionary<string, string>.Empty
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

    private static YamlMappingNode Jobs()
    {
        var yaml = new YamlStream();
        using var reader = File.OpenText(Path.Combine(SourceScan.FindRepoRoot(), Workflow));
        yaml.Load(reader);
        return (YamlMappingNode)((YamlMappingNode)yaml.Documents[0].RootNode).Children[new YamlScalarNode("jobs")];
    }

    private static YamlMappingNode FindStep(string job, string key, string value) =>
        ((YamlSequenceNode)((YamlMappingNode)Jobs().Children[new YamlScalarNode(job)])
            .Children[new YamlScalarNode("steps")]).Children.Cast<YamlMappingNode>()
        .Single(step => step.Children.TryGetValue(new YamlScalarNode(key), out var found) && found.ToString() == value);

    private static string Step(string job, string key, string value) =>
        FindStep(job, key, value).Children[new YamlScalarNode("run")].ToString();

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
