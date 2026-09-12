---
Name: The Deployment record is the one input
Category: Feature
Description: An instance is declared once, as a Deployment record. Aspire and Helm render from it, the portal receives it as configuration (Deployment:Record), and the AppHost builds it fluently — AddMemex("memex").WithImage(…).WithPluginRepo(…).PreInstall(…).WithSignIn(…) — with a parity table the tests hold the code to. The adapter's second copy of the record (MemexOptions) is gone, and generating the Helm chart from Aspire is retired.
Icon: Cloud
Order: -20260908
---

# The Deployment record is the one input

Until now an instance was declared in two places that could not see each other: the
`Hosting/Deployment` record on the control instance (what the Helm chart renders from) and the
Aspire adapter's own `MemexOptions` (half the fields, different names). On 2026-09-06 the record and
the rendered overlay of one instance disagreed in **41 places** that nothing reported.

**There is one declaration now.** The record — `DeploymentContent`, in the dependency-free assembly
`MeshWeaver.Deployment.Contract` — is what the Hosting module renders Helm from, what the Aspire
adapter derives a local run from, and what the portal binds at boot from one configuration value,
`Deployment:Record` (`Deployment__Record` as environment), beside the per-key surface both routes
derive from it with the same code.

- **Build it fluently.** `builder.AddMemex("memex").WithImage(…).WithPluginRepo(…).PreInstall(…)
  .WithRequiredModule(…).WithVolume(…).WithKeyVault(…, secrets => secrets.Map(…)).WithSignIn(…)
  .WithEmail(…).WithAi(a => a.OpenRouter(…).Tiers(…)).WithReplicas(2).WithResources(…)
  .WithStartupProbe(…).WithOperator(…).WithPortalConfig(…)`. Every method is a pure transform of the
  record — the same calls serve the setup wizard, the template generator and a test — and every
  record field is reachable. `AddMemex(name, record)` and `AddMemexFromFile(name, path)` start from
  a record you have; `PublishRecord(path)` writes the final one, ready for a `Provision` action.
  Secrets never touch the record: `WithSecret(key, parameter)` under Aspire, Key Vault names on the
  record for Kubernetes.
- **The parity table is a test.** [ConfiguringAnInstanceFromAspire](/Doc/Architecture/ConfiguringAnInstanceFromAspire)
  lists method → record field → Helm value → config key, and `RendererParityTest` fails when the
  code and the page disagree. Three real fleet records round-trip through the contract with every
  field intact, and the `memex` record renders the same keys for Helm and for Aspire except the
  three differences the options declare.
- **Retired: generating Helm from Aspire** (#3646). The duplication was the adapter's copy, not the
  chart; the chart's operational contract is not expressible in Aspire's publisher. Aspire emits a
  record, never a chart.

The rule, in one line, now heads [Deployment](/Doc/Architecture/Deployment): *the Deployment record
is the ONE input; Aspire and Helm render from it; the image receives it as configuration; Aspire
emits a record, never a chart.*
