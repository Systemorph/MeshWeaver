using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// The parity table in <c>Doc/Architecture/ConfiguringAnInstanceFromAspire</c> (method → record
/// field → Helm value → config key) IS the contract between the fluent surface, the record and the
/// two renderers, and this test holds the code to the page: every method the table names exists,
/// every method the code has is in the table, every field the table names is on the record, every
/// public field of the record is reachable from the table, and every config key the table names is
/// emitted by the derivation both renderers share.
/// </summary>
public class RendererParityTest
{
    private static readonly Regex Row = new(@"^\|\s*`(?<method>[^`]+)`\s*\|(?<fields>[^|]*)\|(?<helm>[^|]*)\|(?<keys>[^|]*)\|", RegexOptions.Compiled);
    private static readonly Regex Code = new(@"`([^`]+)`", RegexOptions.Compiled);

    private sealed record TableRow(string Method, string[] Fields, string[] Keys, int Line);

    private static readonly Regex StateRow = new(@"^\|\s*`(?<field>[^`]+)`\s*\|", RegexOptions.Compiled);

    /// <summary>The "Fields no method sets" table: record fields the operator writes, exempt from fluent reachability.</summary>
    private static HashSet<string> ReadStateFields()
    {
        var root = RepoRoot()!;
        var page = Path.Combine(root, "src", "MeshWeaver.Documentation", "Data", "Architecture", "ConfiguringAnInstanceFromAspire.md");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var inTable = false;
        foreach (var line in File.ReadAllLines(page))
        {
            if (line.StartsWith("| Field | Written by |", StringComparison.Ordinal)) { inTable = true; continue; }
            if (!inTable) continue;
            if (!line.StartsWith("|", StringComparison.Ordinal)) break;
            if (line.StartsWith("|---", StringComparison.Ordinal)) continue;
            var m = StateRow.Match(line);
            if (m.Success) fields.Add(m.Groups["field"].Value);
        }
        return fields;
    }

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private static List<TableRow> ReadTable()
    {
        var root = RepoRoot();
        Assert.SkipWhen(root is null, "repository tree not reachable from the test bin — the parity table lives in the doc tree");
        var page = Path.Combine(root!, "src", "MeshWeaver.Documentation", "Data", "Architecture", "ConfiguringAnInstanceFromAspire.md");
        Assert.True(File.Exists(page), $"the parity page is not at {page}");

        var rows = new List<TableRow>();
        var lines = File.ReadAllLines(page);
        var inTable = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("| Method |", StringComparison.Ordinal)) { inTable = true; continue; }
            if (!inTable) continue;
            if (!line.StartsWith("|", StringComparison.Ordinal)) break;
            if (line.StartsWith("|---", StringComparison.Ordinal)) continue;
            var m = Row.Match(line);
            Assert.True(m.Success, $"line {i + 1} of the parity table does not parse as a row: {line}");
            var method = m.Groups["method"].Value;
            method = method[..method.IndexOf('(')].Trim();
            var fields = Code.Matches(m.Groups["fields"].Value).Select(x => x.Groups[1].Value).ToArray();
            var keys = Code.Matches(m.Groups["keys"].Value).Select(x => x.Groups[1].Value).ToArray();
            rows.Add(new TableRow(method, fields, keys, i + 1));
        }
        Assert.True(rows.Count >= 40, $"the parity table has {rows.Count} rows — the fluent surface has far more methods");
        return rows;
    }

    private static IEnumerable<MethodInfo> RecordTransforms() =>
        typeof(DeploymentRecordExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(DeploymentContent));

    [Fact]
    public void EveryMethodInTheTableExistsAndEveryMethodIsInTheTable()
    {
        var rows = ReadTable();
        var code = RecordTransforms().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var table = rows.Select(r => r.Method).ToHashSet(StringComparer.Ordinal);

        var phantom = table.Except(code).ToList();
        Assert.True(phantom.Count == 0, "methods the table names that DeploymentRecordExtensions does not have: " + string.Join(", ", phantom));

        var undocumented = code.Except(table).ToList();
        Assert.True(undocumented.Count == 0, "record transforms missing from the parity table (add a row): " + string.Join(", ", undocumented));
    }

    [Fact]
    public void EveryFieldInTheTableIsOnTheRecordAndEveryRecordFieldIsInTheTable()
    {
        var rows = ReadTable();
        var bad = new List<string>();
        foreach (var row in rows)
            foreach (var field in row.Fields)
                if (field != "—" && Resolve(typeof(DeploymentContent), field) is null)
                    bad.Add($"line {row.Line}: `{field}`");
        Assert.True(bad.Count == 0, "fields the table names that the record does not have: " + string.Join("; ", bad));

        var documentedTopLevel = rows.SelectMany(r => r.Fields).Select(f => f.Split('.', '[')[0]).ToHashSet(StringComparer.Ordinal);
        var stateFields = ReadStateFields();
        Assert.True(stateFields.Count is >= 1 and <= 5, $"the operator-written table lists {stateFields.Count} fields — it is an exemption list, and it is growing");
        documentedTopLevel.UnionWith(stateFields);
        var recordFields = typeof(DeploymentContent).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite).Select(p => p.Name).ToList();
        Assert.True(recordFields.Count >= 40, $"DeploymentContent exposes {recordFields.Count} fields — the transcription is incomplete");
        var unreachable = recordFields.Except(documentedTopLevel).ToList();
        Assert.True(unreachable.Count == 0, "record fields no row of the parity table reaches: " + string.Join(", ", unreachable));
    }

    [Fact]
    public void EveryConfigKeyInTheTableIsEmittedByTheSharedDerivation()
    {
        var rows = ReadTable();
        var record = FullyPopulated();
        var helm = DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Helm);
        var aspire = DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Aspire("http://localhost:8080"));
        var emitted = helm.Keys.Union(aspire.Keys).ToHashSet(StringComparer.Ordinal);

        var checkedKeys = 0;
        var missing = new List<string>();
        foreach (var row in rows)
            foreach (var key in row.Keys)
            {
                if (key == "—") continue;
                checkedKeys++;
                // A `Prefix__*` names a family (the extras, the model providers); one member suffices.
                var ok = key.EndsWith("*", StringComparison.Ordinal)
                    ? emitted.Any(k => k.StartsWith(key[..^1], StringComparison.Ordinal))
                    : emitted.Contains(key);
                if (!ok) missing.Add($"line {row.Line}: {key}");
            }

        Assert.True(checkedKeys >= 40, $"only {checkedKeys} config keys named in the table");
        Assert.True(missing.Count == 0,
            $"config keys the table promises that neither renderer emits for a fully populated record ({emitted.Count} keys emitted):\n" + string.Join("\n", missing));
    }

    /// <summary>A record with every typed block set — so a key that depends on a block being present is emitted.</summary>
    internal static DeploymentContent FullyPopulated() =>
        new DeploymentContent()
            .WithHost("portal.example.com", "example.com")
            .WithNamespace("portal", "portal")
            .WithCluster("aks-fleet")
            .WithOwner("owner", "purpose")
            .WithConfigRepository("Systemorph/Memex", "environments/portal")
            .WithGrafana("https://grafana.example.com")
            .WithDatabase("portal", server: "pg-portal", username: "memex", host: "pg-portal.postgres.database.azure.com", port: 5432, connectionSecret: "portal-pg-connection")
            .WithImage("ghcr.io/systemorph/memex-portal-ai", tag: "3.1.0", pullSecret: "ghcr-pull")
            .WithUpdatePolicy("stable").WithMinRollInterval("00:30:00").WithAutoRecycleOnStaleBuild(true)
            .WithPluginRepo("plugins", "https://github.com/Systemorph/MeshWeaver.Plugins", gitRef: "main")
            .PreInstall("MeshWeaver.Plugins/Hosting")
            .WithRequiredModule("MeshWeaver.Hosting.Postgres")
            .WithReplicas(2).WithOrleansClustering("AdoNet").WithHttpPort(8080)
            .WithResources("500m", "2Gi", "2", "8Gi")
            .WithAutoscaling(true, 1, 4, 70, 80)
            .WithVolume("data", "/data", size: "128Gi", storageClass: "azurefile", accessMode: "ReadWriteMany")
            .WithIngress("nginx", "portal-tls", sessionAffinity: true)
            .WithStartupProbe(10, 5, 60)
            .WithDrain(30, 10)
            .WithStorageLayout(s => s with { ClaudeCodeConfigDirRoot = "/mnt/users" })
            .WithStorageAccount("stportal", "portal-storage-connection")
            .WithGate("python", "ghcr.io/systemorph/gate-python:1")
            .WithKeyVault("kv-portal", "portal-")
            .WithKeyVaultSecrets(s => s.Map("Email__ClientSecret", "email-clientsecret"), tenantId: "tenant", identityClientId: "identity")
            .WithVaultValuesKeys("Ai__KeyProtection__MasterKey")
            .WithInlineEnv("LOG_LEVEL", "Information")
            .WithSignIn("Custom", microsoftClientId: "ms", microsoftTenantId: "tenant", googleClientId: "google", linkedInClientId: "linkedin", appleClientId: "apple", enableDevLogin: false)
            .WithEmail(true, "portal@example.com", "email-client", "tenant", useManagedIdentity: false, inboundEnabled: true, webhookBaseUrl: "https://portal.example.com", inboundForwardAddress: "inbox@example.com")
            .WithGitHubApp("gh-client", "12345", "Systemorph")
            .WithSocialLinkedIn("linkedin")
            .WithAi(a => a.OpenRouter(["anthropic/claude-sonnet-4"]).Anthropic(["claude-sonnet-4"], enabled: true).AzureFoundry(["gpt-5"], enabled: true).AzureAis(["deepseek"]).Tiers("heavy", "standard", "light", "utility"))
            .WithOperator(true, "hosting", "hosting-operator", "ghcr.io/systemorph/hosting-operator:1")
            .WithTelemetry("http://otel:4317", "grpc")
            .WithBackupStore("stbackups")
            .WithIdlePolicy(30, 90)
            .WithGracePeriod(14)
            .WithWebhookInbox("GitHub", "GitHub__WebhookSecret")
            .WithPortalConfig("Embedding__Endpoint", "https://openrouter.ai/api/v1");

    /// <summary>`Top`, `Top.Nested`, `List[].Element` — resolved against the record's CLR shape.</summary>
    private static PropertyInfo? Resolve(Type type, string path)
    {
        PropertyInfo? property = null;
        foreach (var raw in path.Split('.'))
        {
            var segment = raw.EndsWith("[]", StringComparison.Ordinal) ? raw[..^2] : raw;
            property = type.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
            if (property is null) return null;
            type = property.PropertyType;
            if (raw.EndsWith("[]", StringComparison.Ordinal))
                type = type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type) ? type.GetGenericArguments()[0] : type;
            type = Nullable.GetUnderlyingType(type) ?? type;
        }
        return property;
    }
}
