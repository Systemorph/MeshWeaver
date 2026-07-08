namespace Memex.Portal.Shared.Instances;

/// <summary>
/// Generates the vetted, copy-pasteable command sequence to provision a NEW instance on the shared
/// AKS cluster — the "guided" create flow: it produces a plan, it does NOT mutate any infrastructure.
/// Mirrors the runbook in Doc/Architecture/OnboardingNewEnvironment.md, parameterized by the admin's
/// inputs. Pure + unit-tested; the layout area renders the result as a markdown code block.
/// </summary>
public static class InstanceProvisioningPlan
{
    /// <summary>k8s namespace / DNS label rules: lowercase alphanumeric + '-', 1..63, no leading/trailing '-'.</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 63
        && System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9]([a-z0-9-]*[a-z0-9])?$");

    /// <summary>
    /// The provisioning plan for a new instance. <paramref name="name"/> is the namespace + env id;
    /// <paramref name="domain"/> the public host; <paramref name="databaseName"/> the DB on the
    /// shared server (defaults to a name derived from <paramref name="name"/>). Returns markdown.
    /// </summary>
    public static string Generate(string? name, string? domain, string? databaseName, InstancesOptions o)
    {
        var problems = new List<string>();
        if (!IsValidName(name))
            problems.Add("- **Name** must be a valid k8s namespace: lowercase letters/digits/'-', ≤63 chars, no leading/trailing '-'.");
        if (string.IsNullOrWhiteSpace(domain))
            problems.Add("- **Domain** is required (the public host, e.g. `acme.meshweaver.cloud`).");
        if (problems.Count > 0)
            return "### Cannot generate a plan yet\n\n" + string.Join("\n", problems);

        var ns = name!.Trim();
        var host = domain!.Trim();
        var db = string.IsNullOrWhiteSpace(databaseName)
            ? ns.Replace("-", "")
            : databaseName!.Trim();

        return $$"""
### Provisioning plan for instance `{{ns}}`

> This is a **plan only** — nothing below runs automatically. Review each step, then execute it
> yourself against `{{o.ClusterName}}` (RG `{{o.ResourceGroup}}`). Full reference:
> [OnboardingNewEnvironment](/Doc/Architecture/OnboardingNewEnvironment).

**Target:** namespace `{{ns}}` · host `{{host}}` · database `{{db}}` on `{{o.PostgresServer}}` · image from `{{o.Registry}}`.

**1. Federated credential (bicep) — add the namespace to `portalNamespaces`:**
```bash
# deploy/aks/infra/main.bicep → param portalNamespaces: add '{{ns}}', then:
az deployment group create -g {{o.ResourceGroup}} \
  --template-file deploy/aks/infra/main.bicep \
  --parameters portalNamespaces="['memex','memex-cloud','atioz','{{ns}}']"
```

**2. Database on the shared server:**
```bash
az postgres flexible-server db create \
  -g {{o.ResourceGroup}} -s {{o.PostgresServer}} -d {{db}}
```

**3. Env values (git-ignored) — scaffold `deploy/aks/envs/{{ns}}/values.{{ns}}.yaml`:**
```yaml
ingress:
  host: {{host}}
env:
  MEMEX_DATABASENAME: {{db}}
selfUpdate:
  azureClientId: <portalIdentityClientId>   # the shared UAMI client id (same for every env)
# + TLS secretName, AI + OAuth config, resources — copy from an existing env and adjust.
```

**4. Deploy (helm install + PVCs + Key Vault SecretProviderClass + ingress + TLS):**
```bash
deploy/aks/envs/{{ns}}/deploy.sh
```

**5. DNS + sign-in:** point `{{host}}` at the ingress IP (zone `{{o.DnsZone}}`), then add the OAuth
redirect URIs (`https://{{host}}/signin-microsoft`, `/signin-google`, …) and invitation/email config.

**6. Verify:**
```bash
az aks command invoke -g {{o.ResourceGroup}} -n {{o.ClusterName}} --command \
  "kubectl -n {{ns}} rollout status deployment/memex-portal-deployment --timeout=300s"
curl -sS -o /dev/null -w '%{http_code}\n' https://{{host}}/   # → 200
```

Once rolled, `{{host}}` self-updates from `main` like every other instance.
""";
    }
}
