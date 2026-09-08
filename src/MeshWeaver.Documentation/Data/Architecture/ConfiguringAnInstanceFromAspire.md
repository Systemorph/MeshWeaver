---
Name: Configuring an instance from Aspire
Category: Architecture
Description: The Deployment record is the ONE input — Aspire and Helm render from it, the image receives it as configuration, and Aspire emits a record, never a chart. The fluent builder over the record, how the record reaches the portal (Deployment:Record), and the parity table (method → record field → Helm value → config key) that RendererParityTest holds the code to.
Icon: Cloud
---

# Configuring an instance from Aspire

**The Deployment record is the ONE input.** Aspire and Helm render from it; the image receives it
as configuration (`Deployment:Record`); Aspire emits a record, never a chart. (Maintainer,
2026-09-08: *"yes, must be the only input"* · *"pipe it somehow into the memex image via config ⇒
see how e.g. orleans interfaces"* · *"create fluent language to configure. give possibilities to
configure in the aspire api"* · on generating Helm from Aspire: *"ok. retire"*.)

An instance IS a `DeploymentContent` record — the `Hosting/Deployment` node the control instance
holds at `Deployments/<name>` ([Operating from the portal](/Doc/Architecture/OperatingFromThePortal)).
Before this change the record had a second, adapter-only copy in `MemexOptions` (half its fields,
different names, a hand-maintained chart beside both), and on 2026-09-06 the record and the
rendered overlay disagreed in **41 places** that nothing reported. Now there is one declaration and
three readers of it:

| Reader | Where | What it does with the record |
|---|---|---|
| **The Hosting module** (MeshWeaver.Plugins, `Hosting/**`) | the control instance | renders the Helm values (`HelmValues.Render`) and the operator's catalog file; runs `Provision`, `Roll`, `Reconcile`, `Audit` against it |
| **The Aspire adapter** (`MeshWeaver.Aspire.Hosting.Memex`) | a developer's AppHost, the ACA lanes | derives the local containers, volumes and environment from it; injects it into the portal; writes it out (`PublishRecord`) |
| **The portal itself** | inside the image | binds its own record from configuration at boot (`DeploymentRecordJson.FromConfiguration`) |

The contract they share is one assembly, **`MeshWeaver.Deployment.Contract`** (namespace
`MeshWeaver.Deployment`; `src/MeshWeaver.Deployment.Contract/`): the record and its nested specs,
the fluent builder (`DeploymentRecordExtensions`), the configuration binding
(`DeploymentRecordJson`) and the portal-config derivation the chart and the adapter both use
(`DeploymentPortalConfig`). It has **zero MeshWeaver references** so it can ride inside the
published Aspire package (bundled into its `lib/` folder — no third NuGet id; the maintainer
retired all but two on 2026-09-07), and inside the image it is referenced by
`MeshWeaver.PluginCatalog`, which puts it in the application closure the in-mesh Hosting sources
compile against.

## The fluent surface

Every method is a **pure transform of the record**: it returns a new `DeploymentContent`, leaves
its input untouched, composes, and needs no Aspire — the same calls serve an AppHost, the setup
wizard, the `dotnet new` template generator and a test. Defaults live on the record, never on the
builder: a bare `new DeploymentContent()` already renders a bootable portal (`PostgreSql` graph
store, `Filesystem` backend, port 8080).

```csharp
using MeshWeaver.Deployment;

var record = new DeploymentContent()
    .WithHost("portal.example.com", dnsZone: "example.com")
    .WithNamespace("portal")
    .WithDatabase("portal", server: "pg-portal", connectionSecret: "portal-pg-connection")
    .WithImage("ghcr.io/systemorph/memex-portal-ai", tag: "3.1.0")
    .WithPluginRepo("plugins", "https://github.com/Systemorph/MeshWeaver.Plugins", gitRef: "main")
    .PreInstall("MeshWeaver.Plugins/Hosting")
    .WithRequiredModule("MeshWeaver.Hosting.Postgres")
    .WithVolume("data", "/data", size: "128Gi", storageClass: "azurefile", accessMode: "ReadWriteMany")
    .WithKeyVault("kv-portal", prefix: "portal-")
    .WithKeyVaultSecrets(secrets => secrets.Map("Email__ClientSecret", "email-clientsecret"))
    .WithSignIn(microsoftClientId: "<client id>", microsoftTenantId: "<tenant>")
    .WithEmail(mailboxAddress: "portal@example.com", clientId: "<client id>", tenantId: "<tenant>")
    .WithAi(ai => ai.OpenRouter(["anthropic/claude-sonnet-4"]).Tiers(heavy: "anthropic/claude-opus-4"))
    .WithReplicas(2)
    .WithResources(requestsCpu: "500m", requestsMemory: "2Gi", limitsCpu: "2", limitsMemory: "8Gi")
    .WithStartupProbe(periodSeconds: 10, failureThreshold: 60)
    .WithOperator(ns: "hosting")
    .WithPortalConfig("Embedding__Endpoint", "https://openrouter.ai/api/v1");
```

In an AppHost the entry point stays **`AddMemex`**, and it returns a resource builder that
**carries the record**. Each `With*` on the resource builder delegates one-to-one to the record
transform above, then re-syncs what Aspire derives (images, volumes, replicas); `Configure(record
=> …)` applies any transform the named wrappers do not carry.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddMemex("memex")                       // the default record: default images, one replica, the fleet's three volumes
    .WithImage("ghcr.io/systemorph/memex-portal-ai", tag: "3.1.0")
    .WithOrleansClustering("AdoNet")
    .WithPluginRepo("plugins", "https://github.com/Systemorph/MeshWeaver.Plugins", gitRef: "main")
    .PreInstall("MeshWeaver.Plugins/Hosting")
    .WithSignIn(microsoftClientId: "<client id>", microsoftTenantId: "<tenant>")
    .WithSecret("Authentication__Microsoft__ClientSecret", builder.AddParameter("microsoft-client-secret", secret: true))
    .PublishRecord("deployments/memex.json");   // the FINAL record, written when the application starts

builder.Build().Run();
```

`AddMemex(name, record)` starts from a record you already have; `AddMemexFromFile(name, path)`
reads one (a bare record, or a full mesh node with a `content` object — what `get
Deployments/memex` returns). `MemexOptions` is what is left of the old options type: the default
image registry, the default portal repository, the derived default tag (`<major>-latest`) and
`DefaultRecord(name)`.

**Secrets are never on the record** — it syncs to git. On Kubernetes a secret is a Key Vault
*name* on the record (`keyVaultSecrets`, `vaultValuesKeys`) and a value the CSI driver mounts;
under Aspire it is `WithSecret(configurationKey, parameter)` — an Aspire parameter, or a literal in
development — handed to the container directly. `PublishRecord` never writes one, by construction.

## How the record reaches the image

The way Orleans' Aspire integration hands a silo its clustering — resource → environment → the app
binds from configuration — the adapter injects the record into the portal container as **one
configuration section**:

| Key | Aspire (environment) | Helm (ConfigMap) | Value |
|---|---|---|---|
| `Deployment:Record` | `Deployment__Record` | `config.memex_portal.Deployment__Record` | the whole record, as JSON (`DeploymentRecordJson.Write`) |
| the derived surface | `Storage__*`, `Graph__Storage__*`, `Modules__Required__N`, `Authentication__*`, `Email__*`, … | the same keys, in `config.memex_portal` | `DeploymentPortalConfig.PortalConfig(record, options)` — one derivation, both routes |

It is **one JSON value on purpose**: `DeploymentContent` is an init-only record over immutable
collections, carrying the mesh's `$type`, and none of that binds through the configuration binder
— while one JSON value is byte-identical on a ConfigMap and in an Aspire environment. The portal
reads it with `DeploymentRecordJson.FromConfiguration(configuration)` (null when the section is
absent — a Monolith, a test). The derived per-key surface stays beside it because that is what
every existing `IOptions` reader in the image already binds; the record is the source, the keys
are its projection.

The two routes differ in exactly the places `PortalConfigOptions` names — declared, not
discovered:

| Difference | Helm (`PortalConfigOptions.Helm`) | Aspire (`PortalConfigOptions.Aspire(mcpBaseUrl)`) |
|---|---|---|
| Database keys (`MEMEX_*`) | from the record (`DatabaseServer`, `DatabaseHost`, …) | not emitted — the Aspire Postgres resource injects `ConnectionStrings__memex` / `__orleans` |
| `Mcp__BaseUrl` | the in-cluster portal Service | the endpoint Aspire allocates (substituted at publish) |
| Plugin-catalog boot wiring (`PluginCatalog__*`) | the operator's catalog config file, not the ConfigMap | emitted as environment — Aspire has no second file |

`FluentBuilderTest.TheSameRecordRendersTheSameKeysForHelmAndForAspire` renders the real `memex`
record both ways and asserts the difference is exactly these three.

## The parity table

This table is the contract between the fluent surface, the record and the two renderers.
**`RendererParityTest` (`test/MeshWeaver.Deployment.Contract.Test`) reads it**: every method it
names exists on `DeploymentRecordExtensions`; every record transform the code has is in it; every
field it names is on the record; every public field of the record is reached by some row (or is in
the "written by the operator" table below); and every configuration key it names is emitted by
`DeploymentPortalConfig.PortalConfig` for a fully populated record (`Prefix__*` names a family).
Editing the code without the table, or the table without the code, is red.

Helm values are the paths in the values file `HelmValues.Render` produces (`config.memex_portal.*`
is the portal ConfigMap). A dash means the field is the record's or the operator's alone — it
configures no container.

| Method | Record field | Helm value | Config key |
|---|---|---|---|
| `WithHost(host, dnsZone)` | `Host`, `DnsZone` | `ingress` (host) — the DNS record is the operator's | — |
| `WithNamespace(ns, helmRelease)` | `Namespace`, `HelmRelease` | the release itself (`helm upgrade --install <release> -n <ns>`) | — |
| `WithCluster(cluster)` | `Cluster` | — (which cluster the operator targets) | — |
| `WithOwner(owner, purpose)` | `Owner`, `Purpose` | — | — |
| `WithStatus(status)` | `Status` | — | — |
| `WithNotes(notes)` | `Notes` | — | — |
| `AppendNote(paragraph)` | `Notes` | — | — |
| `WithConfigRepository(repository, configPath)` | `Repository`, `ConfigPath` | — (where the rendered overlay is committed) | — |
| `WithGrafana(baseUrl)` | `GrafanaBaseUrl` | — | — |
| `WithDatabase(database, server, username, host, port, connectionSecret)` | `Database`, `DatabaseServer`, `DatabaseUsername`, `DatabaseHost`, `DatabasePort`, `DatabaseConnectionSecret` | `config.memex_migration.MEMEX_*`, `config.memex_portal.MEMEX_*` | `MEMEX_DATABASENAME`, `MEMEX_HOST`, `MEMEX_PORT`, `MEMEX_USERNAME`, `MEMEX_JDBCCONNECTIONSTRING` (Helm only) |
| `WithInClusterPostgres(enabled)` | `InClusterPostgres` | `postgres.enabled` | — |
| `WithImage(repository, tag, pullSecret, migrationRepository)` | `ImageRepository`, `PinnedImageTag`, `ImagePullSecret`, `MigrationImageRepository` | `portal.image`, `portal.imagePullSecret`, `migration.image`, `selfUpdate.registry` | — |
| `WithPinnedImageTag(tag)` | `PinnedImageTag` | `portal.image` (the tag; the pin IS the roll) | — |
| `WithUpdatePolicy(policy)` | `UpdatePolicy` | — (the self-updater reads the record) | — |
| `WithMinRollInterval(interval)` | `MinRollInterval` | `config.memex_portal.SelfUpdate__MinRollInterval` | `SelfUpdate__MinRollInterval` |
| `WithAutoRecycleOnStaleBuild(enabled)` | `AutoRecycleOnStaleBuild` | `config.memex_portal.Modules__AutoRecycleOnStaleBuild` | `Modules__AutoRecycleOnStaleBuild` |
| `WithPluginRepo(name, url, gitRef, isRegistrySource, secretName)` | `PluginRepos[].Name`, `PluginRepos[].Url`, `PluginRepos[].Ref`, `PluginRepos[].IsRegistrySource`, `PluginRepos[].SecretName` | `pluginCatalog.sources` (the operator's catalog file) | `PluginCatalog__*` (Aspire only) |
| `ClearPluginRepos()` | `PluginRepos` | `pluginCatalog.sources` | — |
| `PreInstall(packageIds)` | `PreInstall` | `pluginCatalog.installByDefault` | `PluginCatalog__*` (Aspire only) |
| `ClearPreInstall()` | `PreInstall` | `pluginCatalog.installByDefault` | — |
| `AsPluginRegistry(isRegistry)` | `IsPluginRegistry` | `registry` (with `WithRegistry`) | — |
| `WithRegistryInstanceId(instanceId)` | `RegistryInstanceId` | — (the instance's key at the registry) | — |
| `WithRequiredModules(assemblies)` | `RequiredModules` | `config.memex_portal.Modules__Required__N` | `Modules__Required__0` |
| `WithRequiredModule(assembly)` | `RequiredModules` | `config.memex_portal.Modules__Required__N` | `Modules__Required__0` |
| `ClearRequiredModules()` | `RequiredModules`, `RequiredModuleSlots` | `config.memex_portal.Modules__Required__N` | — |
| `WithRequiredModuleSlot(slot, assembly)` | `RequiredModuleSlots` | `config.memex_portal.Modules__Required__N` | `Modules__Required__*` |
| `WithReplicas(replicas)` | `Replicas` | `replicas` | — |
| `WithOrleansClustering(clustering)` | `OrleansClustering` | `config.memex_portal.Deployment__Orleans__Clustering` | `Deployment__Orleans__Clustering` |
| `WithHttpPort(port)` | `HttpPort` | `config.memex_portal.ASPNETCORE_HTTP_PORTS` | `ASPNETCORE_HTTP_PORTS` |
| `WithResources(requestsCpu, requestsMemory, limitsCpu, limitsMemory)` | `Resources.Requests.Cpu`, `Resources.Requests.Memory`, `Resources.Limits.Cpu`, `Resources.Limits.Memory` | `resources` | — |
| `WithAutoscaling(enabled, minReplicas, maxReplicas, cpuTarget, memoryTarget)` | `Autoscaling.Enabled`, `Autoscaling.MinReplicas`, `Autoscaling.MaxReplicas`, `Autoscaling.CpuTarget`, `Autoscaling.MemoryTarget` | `keda` | — |
| `WithVolume(name, mountPath, size, claimName, storageClass, accessMode, create)` | `Volumes[].Name`, `Volumes[].MountPath`, `Volumes[].Size`, `Volumes[].ClaimName`, `Volumes[].StorageClass`, `Volumes[].AccessMode`, `Volumes[].Create` | `persistence`, `extraVolumes`, `extraVolumeMounts` (Aspire: a named Docker volume at the mount path) | — |
| `ClearVolumes()` | `Volumes` | `persistence` | — |
| `WithIngress(className, tlsSecret, annotations, sessionAffinity, affinityCookie)` | `Ingress.ClassName`, `Ingress.TlsSecret`, `Ingress.Annotations`, `Ingress.SessionAffinity.Enabled`, `Ingress.SessionAffinity.CookieName` | `ingress` | — |
| `WithPortalNext(enabled, image, replicas, portalOrigin)` | `PortalNext.Enabled`, `PortalNext.Image`, `PortalNext.Replicas`, `PortalNext.PortalOrigin` | `portalNext` | — |
| `WithStartupProbe(periodSeconds, timeoutSeconds, failureThreshold)` | `StartupProbe.PeriodSeconds`, `StartupProbe.TimeoutSeconds`, `StartupProbe.FailureThreshold` | `probes.startup` | — |
| `WithDrain(drainSeconds, shutdownMarginSeconds)` | `Drain.DrainSeconds`, `Drain.ShutdownMarginSeconds` | the pod's termination grace period | — |
| `WithStorageLayout(configure)` | `Storage.Backend`, `Storage.DataRoot`, `Storage.ModulesRoot`, `Storage.ContentPath`, `Storage.ContentName`, `Storage.ContentSourceType`, `Storage.GraphStorageType`, `Storage.GraphStoragePath`, `Storage.ClaudeCodeConfigDirRoot` | `config.memex_portal.*` | `Deployment__Backend`, `Deployment__DataRoot`, `Modules__Root`, `Storage__BasePath`, `Storage__Name`, `Storage__SourceType`, `Graph__Storage__Type`, `Graph__Storage__BasePath`, `ClaudeCode__ConfigDirRoot` |
| `WithStorageAccount(account, connectionSecret)` | `StorageAccount`, `StorageConnectionSecret` | `keyVaultSecrets` (the connection string's vault object) | — |
| `WithGate(language, image, address, enabled)` | `Gates[].Language`, `Gates[].Image`, `Gates[].Address`, `Gates[].Enabled` | `grpc` | — |
| `WithKeyVault(vault, prefix)` | `KeyVault`, `KeyVaultSecretPrefix` | `keyVaultSecrets.vaultName` | — |
| `WithKeyVaultSecrets(map, vaultName, tenantId, identityClientId, className, syncedSecret, volumeName, mountPath)` | `KeyVaultSecrets.VaultName`, `KeyVaultSecrets.TenantId`, `KeyVaultSecrets.IdentityClientId`, `KeyVaultSecrets.Name`, `KeyVaultSecrets.SyncedSecret`, `KeyVaultSecrets.VolumeName`, `KeyVaultSecrets.MountPath`, `KeyVaultSecrets.Secrets[].Key`, `KeyVaultSecrets.Secrets[].VaultSecret` | `keyVaultSecrets` | — (the keys are the secrets' own; values never touch the record) |
| `WithKeyVaultSecretClass(secretClass)` | `KeyVaultSecretClasses` | `keyVaultSecretClasses` | — |
| `WithVaultValuesKeys(keys)` | `VaultValuesKeys` | `extraEnvFrom` | — |
| `WithSecretMount(volumeName, secretProviderClass, syncedSecret, mountPath)` | `SecretMounts[].VolumeName`, `SecretMounts[].SecretProviderClass`, `SecretMounts[].SyncedSecret`, `SecretMounts[].MountPath` | `extraVolumes`, `extraVolumeMounts` | — |
| `WithInlineEnv(key, value, isSecret, shadows, agreesWithShadowed, reason)` | `InlineEnv[].Key`, `InlineEnv[].Value`, `InlineEnv[].IsSecret`, `InlineEnv[].Shadows`, `InlineEnv[].AgreesWithShadowed`, `InlineEnv[].Reason`, `InlineEnv[].RetiredBy` | the env-precedence ledger the `Audit` reads | — |
| `WithSignIn(provider, microsoftClientId, microsoftTenantId, googleClientId, linkedInClientId, appleClientId, enableDevLogin)` | `SignIn.Provider`, `SignIn.MicrosoftClientId`, `SignIn.MicrosoftTenantId`, `SignIn.GoogleClientId`, `SignIn.LinkedInClientId`, `SignIn.AppleClientId`, `SignIn.EnableDevLogin` | `config.memex_portal.Authentication__*` | `Authentication__Provider`, `Authentication__EnableDevLogin`, `Authentication__Microsoft__ClientId`, `Authentication__Microsoft__TenantId`, `Authentication__Google__ClientId`, `Authentication__LinkedIn__ClientId`, `Authentication__Apple__ClientId` |
| `WithEmail(enabled, mailboxAddress, clientId, tenantId, useManagedIdentity, inboundEnabled, webhookBaseUrl, inboundForwardAddress)` | `Email.Enabled`, `Email.MailboxAddress`, `Email.ClientId`, `Email.TenantId`, `Email.UseManagedIdentity`, `Email.InboundEnabled`, `Email.WebhookBaseUrl`, `Email.InboundForwardAddress` | `config.memex_portal.Email__*` | `Email__Enabled`, `Email__MailboxAddress`, `Email__ClientId`, `Email__TenantId`, `Email__UseManagedIdentity`, `Email__InboundEnabled`, `Email__WebhookBaseUrl`, `Email__Inbound__ForwardAddress` |
| `WithGitHubApp(clientId, installationId, installationOwner)` | `GitHubApp.ClientId`, `GitHubApp.InstallationId`, `GitHubApp.InstallationOwner` | `config.memex_portal.GitHub__App__*` | `GitHub__App__ClientId`, `GitHub__App__InstallationId`, `GitHub__App__InstallationOwner` |
| `WithSocialLinkedIn(clientId)` | `SocialLinkedInClientId` | `config.memex_portal.Social__LinkedIn__ClientId` | `Social__LinkedIn__ClientId` |
| `WithAi(configure)` | `Ai.OpenRouter`, `Ai.Anthropic`, `Ai.AzureFoundry`, `Ai.AzureAis`, `Ai.Tiers.Heavy`, `Ai.Tiers.Standard`, `Ai.Tiers.Light`, `Ai.Tiers.Utility` | `config.memex_portal.<Provider>__*`, `config.memex_portal.ModelTier__*` | `OpenRouter__Models__0`, `Anthropic__Models__0`, `AzureFoundry__Models__0`, `AzureAIS__Models__0`, `Features__Ai__Providers__Anthropic`, `Features__Ai__Providers__AzureFoundry`, `ModelTier__Heavy`, `ModelTier__Standard`, `ModelTier__Light`, `ModelTier__Utility` |
| `WithOperator(enabled, ns, serviceAccount, image, environment)` | `Operator.Enabled`, `Operator.Namespace`, `Operator.ServiceAccount`, `Operator.Image`, `Operator.Environment` | `hostingOperator` | `Hosting__Operator__Enabled` |
| `WithRegistry(configure)` | `Registry.Host`, `Registry.Image`, `Registry.AuthImage`, `Registry.Issuer`, `Registry.StorageAccountName`, `Registry.StorageContainer`, `Registry.ServiceAccount`, `Registry.KeyVault`, `Registry.PublisherUsername`, `Registry.PublisherPasswordBcrypt`, `Registry.ValidationUrl`, `Registry.NotificationsUrl`, `Registry.Replicas` | `registry` | — |
| `WithTelemetry(otlpEndpoint, otlpProtocol)` | `Telemetry.OtlpEndpoint`, `Telemetry.OtlpProtocol` | `config.memex_portal.OTEL_*` | `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL` |
| `WithBackupStore(backupStore)` | `BackupStore` | — (the `Backup` action's target) | — |
| `WithIdlePolicy(suspendAfterDays, teardownAfterDays)` | `IdleSuspendDays`, `IdleTeardownDays` | — (the idle reaper) | — |
| `WithGracePeriod(days)` | `GracePeriodDays` | — | — |
| `WithWebhookInbox(target, secretConfigKey)` | `WebhookInbox[].Target`, `WebhookInbox[].SecretConfigKey` | `config.memex_portal.WebhookInbox__Targets__N` | `WebhookInbox__Targets__0`, `WebhookInbox__Targets__0__SecretConfigKey` |
| `WithPortalConfig(key, value)` | `ExtraPortalConfig` | `config.memex_portal.<key>` — the advanced rung, any key the typed surface does not carry | `Embedding__*` (for instance) |

The nested builders these rows lean on: `KeyVaultSecretsSpec.Map(key, vaultSecret)`;
`AiProviders.OpenRouter(models, endpoint, order, enabled)` / `.Anthropic(…)` / `.AzureFoundry(…)`
/ `.AzureAis(…)` / `.Tiers(heavy, standard, light, utility)`; `WithStorageLayout(s => s with {…})`
and `WithRegistry(r => r with {…})` take the spec record directly.

Fields no method sets — the operator writes them, or they are a legacy shape kept for records that
still carry it:

| Field | Written by |
|---|---|
| `SuspendedAt` | the `Suspend` / `Reactivate` actions |
| `SuspensionReason` | the `Suspend` action |
| `WebhookInboxTargets` | legacy of `WebhookInbox` — read when the typed list is empty, never written |

## Retired: generating the Helm chart from Aspire

Core [#3646](https://github.com/Systemorph/MeshWeaver/issues/3646) asked for the chart to be
generated from the Aspire model through Aspire's Kubernetes/Helm publisher. That direction is
**retired** (maintainer, 2026-09-08: *"ok. retire"*), for measured reasons:

- **The chart's operational contract is not expressible in Aspire's publisher.** The publisher
  emits a Deployment, a Service and an Ingress per container. The chart renders what an instance
  actually needs to run on the fleet: the run-once migration `Job` that gates the portal
  (`DbVersionGate`), the `SecretProviderClass` per Key Vault class and the CSI mounts, KEDA
  scaling, the startup probe and drain contract, the hosting operator and its service account,
  the hosted registry, the language-gate sidecars, session affinity and the per-ingress-class
  annotations. Each of these is a field on the record and a section of `HelmValues.Render`;
  none is a container-level property a generic publisher could derive.
- **The duplication it meant to remove was the adapter's own second copy of the record**, not
  the chart. On 2026-09-06 the record and the rendered overlay disagreed in 41 places because the
  values were declared twice (`MemexOptions` and the record) and rendered by two hands. With the
  record as the one input the chart is a *renderer* of it, exactly like the adapter — the
  duplication is gone without generating anything.
- **What Aspire does emit is the record** (`PublishRecord`), which the control instance turns
  into a chart release through `Provision` — so the developer path and the fleet path meet at the
  record, not at a YAML file.

`Aspire.Hosting.Kubernetes` is therefore not referenced by the deploy AppHost
(`deploy/aspire/Memex.Deploy.AppHost`), and `--mode kubernetes` is gone.

## What is still open

- **The Plugins half.** The in-mesh Hosting module (`Hosting/Deployment/Source/*`) still compiles
  its own copy of `DeploymentContent`; its PR replaces that copy with `using
  MeshWeaver.Deployment;` and makes `HelmValues.PortalConfig` delegate to
  `DeploymentPortalConfig`. It stays a DRAFT until the fleet's images carry this assembly (merging
  to Plugins main edits live portals — a registration syncs sources at main's HEAD).
- **German labels.** The in-mesh record carries `[Translation("de", …)]` on every property; the
  contract cannot (the attribute is read by concrete type from `MeshWeaver.Messaging.Contract`,
  which is not a published package). The pairs are preserved in the appendix below, and return to
  the declarations once the localizer reads a catalog for declarations.

## Labels

The property labels of the record, English (`[Description]`, on the contract) and German (the
in-mesh `[Translation]` texts, preserved here until the catalog follow-up above).

| Record | Property | English (`[Description]`) | German |
|---|---|---|---|
| `DeploymentContent` | `ImagePullSecret` | Image pull Secret name in the instance namespace — required when the image registry is not ACR | Name des Image-Pull-Secrets im Instanz-Namespace — Pflicht, wenn die Image-Registry nicht ACR ist |
| `DeploymentContent` | `RegistryInstanceId` | Instance id at the plugin registry — blank means the deployment id | Instanz-ID in der Plugin-Registry — leer bedeutet die Deployment-ID |
| `DeploymentContent` | `DatabaseHost` | Database host (FQDN) — blank derives it from the server name | Datenbank-Host (FQDN) — leer leitet ihn vom Servernamen ab |
| `DeploymentContent` | `DatabasePort` | Database port | Datenbank-Port |
| `DeploymentContent` | `DatabaseUsername` | Database user | Datenbank-Benutzer |
| `DeploymentContent` | `InClusterPostgres` | Run an in-cluster Postgres (self-host only) | In-Cluster-Postgres betreiben (nur Selbst-Hosting) |
| `DeploymentContent` | `MigrationImageRepository` | Migration image repository — blank derives it from the portal's | Repository des Migrations-Images — leer leitet es vom Portal ab |
| `DeploymentContent` | `HttpPort` | HTTP port | HTTP-Port |
| `DeploymentContent` | `Resources` | Resources (RAM / CPU) | Ressourcen (RAM / CPU) |
| `DeploymentContent` | `Autoscaling` | Autoscaling | Automatische Skalierung |
| `DeploymentContent` | `Volumes` | Volumes | Volumes |
| `DeploymentContent` | `Ingress` | Ingress | Ingress |
| `DeploymentContent` | `PortalNext` | React front end at /next | React-Oberfläche unter /next |
| `DeploymentContent` | `KeyVaultSecrets` | Key Vault secrets (declared by name) | Key-Vault-Secrets (nach Namen deklariert) |
| `DeploymentContent` | `KeyVaultSecretClasses` | Additional Key Vault secret classes (a second SecretProviderClass, and further) | Weitere Key-Vault-Secret-Klassen (eine zweite SecretProviderClass und weitere) |
| `DeploymentContent` | `VaultValuesKeys` | Keys the chart's own Secret supplies, from the Key Vault values half (names only) | Schlüssel aus dem eigenen Secret des Charts, aus der Key-Vault-Wertehälfte (nur Namen) |
| `DeploymentContent` | `InlineEnv` | Inline env entries on the live Deployment (declarative — renders nothing) | Inline-Env-Einträge auf dem laufenden Deployment (deklarativ — rendert nichts) |
| `DeploymentContent` | `Gates` | Language gate sidecars | Sprach-Gate-Sidecars |
| `DeploymentContent` | `SecretMounts` | Key Vault secret mounts (legacy — hand-made SecretProviderClass by name) | Key-Vault-Secret-Einhängungen (veraltet — handgemachte SecretProviderClass nach Namen) |
| `DeploymentContent` | `Ai` | AI providers | KI-Anbieter |
| `DeploymentContent` | `SignIn` | Sign-in | Anmeldung |
| `DeploymentContent` | `Email` | Email | E-Mail |
| `DeploymentContent` | `GitHubApp` | GitHub App | GitHub-App |
| `DeploymentContent` | `Operator` | Hosting operator | Hosting-Operator |
| `DeploymentContent` | `Registry` | Container registry this instance hosts (the public instance only) | Container-Registry, die diese Instanz betreibt (nur die öffentliche Instanz) |
| `DeploymentContent` | `Telemetry` | Telemetry | Telemetrie |
| `DeploymentContent` | `Drain` | Rollout drain | Rollout-Drain |
| `DeploymentContent` | `StartupProbe` | Startup probe | Start-Prüfung |
| `DeploymentContent` | `Storage` | Storage layout | Speicherlayout |
| `DeploymentContent` | `OrleansClustering` | Orleans clustering — blank derives it from the replica count | Orleans-Clustering — leer leitet es aus der Replikatzahl ab |
| `DeploymentContent` | `MinRollInterval` | Minimum interval between self-update rolls | Mindestabstand zwischen Selbst-Update-Rollouts |
| `DeploymentContent` | `AutoRecycleOnStaleBuild` | Auto-recycle on a stale NodeType build | Bei veraltetem NodeType-Build automatisch neu laden |
| `DeploymentContent` | `RequiredModuleSlots` | Boot modules at explicit slots (by-index override) | Boot-Module an expliziten Slots (Überschreiben nach Index) |
| `DeploymentContent` | `WebhookInboxTargets` | Webhook inbox targets, by slot (legacy — prefer the typed slots) | Webhook-Eingangsziele, nach Slot (veraltet — die typisierten Slots bevorzugen) |
| `DeploymentContent` | `WebhookInbox` | Webhook inbox slots: target + the config key holding its HMAC secret | Webhook-Eingangs-Slots: Ziel + Konfigurationsschlüssel des HMAC-Geheimnisses |
| `DeploymentContent` | `SocialLinkedInClientId` | Social LinkedIn client id | LinkedIn-Client-ID des Social-Plugins |
| `DeploymentContent` | `ExtraPortalConfig` | Extra portal configuration (Section__Key = value) | Zusätzliche Portal-Konfiguration (Section__Key = Wert) |
| `WebhookInboxSlot` | `Target` | Target path the slot routes deliveries to | Zielpfad, an den der Slot Zustellungen weiterleitet |
| `WebhookInboxSlot` | `SecretConfigKey` | Configuration key holding the slot's HMAC secret (blank = unverified) | Konfigurationsschlüssel des HMAC-Geheimnisses (leer = unverifiziert) |
| `RegistrySpec` | `Host` | Registry host (e.g. cr.meshweaver.cloud) | Registry-Host (z. B. cr.meshweaver.cloud) |
| `RegistrySpec` | `Image` | Registry image (repository:tag) | Registry-Image (Repository:Tag) |
| `RegistrySpec` | `AuthImage` | Token-authentication image (repository:tag) | Token-Authentifizierungs-Image (Repository:Tag) |
| `RegistrySpec` | `Issuer` | Token issuer — blank means memex-registry | Token-Aussteller — leer bedeutet memex-registry |
| `RegistrySpec` | `StorageAccountName` | Storage account holding the image blobs (name only) | Storage-Konto mit den Image-Blobs (nur Name) |
| `RegistrySpec` | `StorageContainer` | Blob container — blank means registry | Blob-Container — leer bedeutet registry |
| `RegistrySpec` | `ServiceAccount` | ServiceAccount the registry pods run as | ServiceAccount, unter dem die Registry-Pods laufen |
| `RegistrySpec` | `KeyVault` | Key Vault objects (names only) | Key-Vault-Objekte (nur Namen) |
| `RegistrySpec` | `PublisherUsername` | Publisher username — blank means publisher | Publisher-Benutzername — leer bedeutet publisher |
| `RegistrySpec` | `PublisherPasswordBcrypt` | Publisher password, bcrypt hash — never the password itself | Publisher-Passwort als bcrypt-Hash — nie das Passwort selbst |
| `RegistrySpec` | `ValidationUrl` | Portal URL that validates a consumer's key — blank means the public instance's token exchange | Portal-URL zur Prüfung eines Konsumenten-Schlüssels — leer bedeutet den Token-Austausch der öffentlichen Instanz |
| `RegistrySpec` | `NotificationsUrl` | Notification endpoint URL — blank means none | URL des Benachrichtigungs-Endpunkts — leer bedeutet keine |
| `RegistrySpec` | `Replicas` | Replicas — blank means 1 | Replikate — leer bedeutet 1 |
| `RegistryKeyVaultSpec` | `Name` | Key Vault name | Key-Vault-Name |
| `RegistryKeyVaultSpec` | `TenantId` | Tenant id — blank means the record's keyVaultSecrets tenant | Tenant-ID — leer bedeutet der Tenant aus keyVaultSecrets |
| `RegistryKeyVaultSpec` | `IdentityClientId` | Identity client id — blank means the record's keyVaultSecrets identity | Client-ID der Identität — leer bedeutet die Identität aus keyVaultSecrets |
| `RegistryKeyVaultSpec` | `CertObject` | Vault object: token-signing certificate | Vault-Objekt: Zertifikat zum Signieren der Tokens |
| `RegistryKeyVaultSpec` | `KeyObject` | Vault object: token-signing private key | Vault-Objekt: privater Schlüssel zum Signieren der Tokens |
| `RegistryKeyVaultSpec` | `HttpSecretObject` | Vault object: registry HTTP secret | Vault-Objekt: HTTP-Secret der Registry |
| `RegistryKeyVaultSpec` | `NotificationSecretObject` | Vault object: notification bearer secret | Vault-Objekt: Bearer-Secret für Benachrichtigungen |
| `ResourceQuantities` | `Cpu` | CPU (e.g. 4 or 500m) | CPU (z. B. 4 oder 500m) |
| `ResourceQuantities` | `Memory` | Memory (e.g. 8Gi) | Arbeitsspeicher (z. B. 8Gi) |
| `ResourceEnvelope` | `Requests` | Requests — guaranteed and used for scheduling | Anforderungen — garantiert, bestimmt die Platzierung |
| `ResourceEnvelope` | `Limits` | Limits — the ceiling (memory above it is an OOM kill) | Limits — Obergrenze (Speicher darüber wird abgebrochen) |
| `Autoscaling` | `Enabled` | Autoscale with KEDA | Mit KEDA automatisch skalieren |
| `Autoscaling` | `MinReplicas` | Minimum replicas (the HA floor; 2 seeds the Orleans cluster) | Minimale Replikate (HA-Untergrenze; 2 startet den Orleans-Cluster) |
| `Autoscaling` | `MaxReplicas` | Maximum replicas | Maximale Replikate |
| `Autoscaling` | `CpuTarget` | CPU target % (scale up past this) | CPU-Ziel in % (darüber wird hochskaliert) |
| `Autoscaling` | `MemoryTarget` | Memory target % | Speicher-Ziel in % |
| `VolumeClaim` | `Name` | Role (data, users, content, attachments, workspace, …) | Rolle (data, users, content, attachments, workspace, …) |
| `VolumeClaim` | `ClaimName` | PersistentVolumeClaim name — blank means ephemeral or unmounted | Name des PersistentVolumeClaim — leer heißt flüchtig oder nicht eingehängt |
| `VolumeClaim` | `MountPath` | Mount path | Einhängepfad |
| `VolumeClaim` | `Size` | Size (e.g. 64Gi) | Größe (z. B. 64Gi) |
| `VolumeClaim` | `StorageClass` | Storage class (RWX is required above one replica) | Storage-Klasse (RWX ist ab mehr als einem Replikat Pflicht) |
| `VolumeClaim` | `AccessMode` | Access mode (ReadWriteMany for shared volumes) | Zugriffsmodus (ReadWriteMany für gemeinsame Volumes) |
| `VolumeClaim` | `Create` | Create the PersistentVolumeClaim — only on a record whose claims do not exist yet | PersistentVolumeClaim anlegen — nur bei einem Datensatz, dessen Claims noch nicht existieren |
| `SessionAffinity` | `Enabled` | Pin sessions to one pod | Sitzungen an einen Pod binden |
| `SessionAffinity` | `CookieName` | Affinity cookie name | Name des Affinitäts-Cookies |
| `SessionAffinity` | `NginxAnnotations` | nginx annotations | nginx-Annotationen |
| `SessionAffinity` | `AgicAnnotations` | Application Gateway annotations | Application-Gateway-Annotationen |
| `IngressSpec` | `ClassName` | Ingress class | Ingress-Klasse |
| `IngressSpec` | `TlsSecret` | TLS secret name — blank means {namespace}-tls | Name des TLS-Secrets — leer heißt {namespace}-tls |
| `IngressSpec` | `Annotations` | Ingress annotations | Ingress-Annotationen |
| `IngressSpec` | `SessionAffinity` | Session affinity | Sitzungsaffinität |
| `PortalNextSpec` | `Enabled` | Deploy the React front end at /next | React-Oberfläche unter /next bereitstellen |
| `PortalNextSpec` | `Image` | Container image (repository:tag) — its own release line, never derived | Container-Image (Repository:Tag) — eigene Release-Linie, nie abgeleitet |
| `PortalNextSpec` | `Replicas` | Replicas — blank means the chart's 1 | Replikate — leer heißt 1 (Standard des Charts) |
| `PortalNextSpec` | `PortalOrigin` | Portal origin for server-side rendering — blank means the in-cluster Service | Portal-Origin für serverseitiges Rendern — leer heißt der clusterinterne Service |
| `KeyVaultSecretRef` | `Key` | Configuration key it lands as (Section__Key, e.g. Email__ClientSecret) | Konfigurationsschlüssel, unter dem es ankommt (Section__Key, z. B. Email__ClientSecret) |
| `KeyVaultSecretRef` | `VaultSecret` | Key Vault secret name — blank derives it as {prefix}{Section}-{Key} | Name des Key-Vault-Secrets — leer leitet ihn als {prefix}{Section}-{Key} ab |
| `KeyVaultSecretsSpec` | `VaultName` | Key Vault name — blank means the record's keyVault | Name des Key Vault — leer heißt der keyVault des Datensatzes |
| `KeyVaultSecretsSpec` | `TenantId` | Tenant id of the vault | Mandanten-ID des Vaults |
| `KeyVaultSecretsSpec` | `IdentityClientId` | Client id of the identity that reads the vault (the Key Vault Secrets Provider add-on's identity) | Client-ID der Identität, die den Vault liest (Identität des Key-Vault-Secrets-Provider-Add-ons) |
| `KeyVaultSecretsSpec` | `Name` | SecretProviderClass name — blank means memex-portal-keyvault | Name der SecretProviderClass — leer heißt memex-portal-keyvault |
| `KeyVaultSecretsSpec` | `SyncedSecret` | Synced Kubernetes Secret name — blank means the SecretProviderClass name | Name des synchronisierten Kubernetes-Secrets — leer heißt der Name der SecretProviderClass |
| `KeyVaultSecretsSpec` | `VolumeName` | Volume name — blank means kv-secrets | Volume-Name — leer heißt kv-secrets |
| `KeyVaultSecretsSpec` | `MountPath` | Mount path — blank means /mnt/secrets-store | Einhängepfad — leer heißt /mnt/secrets-store |
| `KeyVaultSecretsSpec` | `Secrets` | The secrets, by name — configuration key and vault object | Die Secrets, nach Namen — Konfigurationsschlüssel und Vault-Objekt |
| `KeyVaultSecretMount` | `VolumeName` | Volume name | Volume-Name |
| `KeyVaultSecretMount` | `SecretProviderClass` | SecretProviderClass | SecretProviderClass |
| `KeyVaultSecretMount` | `SyncedSecret` | Synced Kubernetes Secret name | Name des synchronisierten Kubernetes-Secrets |
| `KeyVaultSecretMount` | `MountPath` | Mount path | Einhängepfad |
| `ModelProvider` | `Endpoint` | Endpoint — empty turns the provider off | Endpunkt — leer schaltet den Anbieter ab |
| `ModelProvider` | `Models` | Models, by slot | Modelle, nach Slot |
| `ModelProvider` | `Order` | Order among providers (0 first) | Reihenfolge unter den Anbietern (0 zuerst) |
| `ModelProvider` | `Enabled` | Enabled | Aktiviert |
| `ModelTiers` | `Heavy` | Heavy tier model | Modell der Stufe „Heavy“ |
| `ModelTiers` | `Standard` | Standard tier model | Modell der Stufe „Standard“ |
| `ModelTiers` | `Light` | Light tier model | Modell der Stufe „Light“ |
| `ModelTiers` | `Utility` | Utility tier model | Modell der Stufe „Utility“ |
| `AiProviders` | `Anthropic` | Anthropic | Anthropic |
| `AiProviders` | `AzureAis` | Azure AI Services | Azure AI Services |
| `AiProviders` | `AzureFoundry` | Azure AI Foundry (models only) | Azure AI Foundry (nur Modelle) |
| `AiProviders` | `OpenRouter` | OpenRouter | OpenRouter |
| `AiProviders` | `Tiers` | Model tiers | Modellstufen |
| `SignInSpec` | `Provider` | Provider (Custom) | Anbieter (Custom) |
| `SignInSpec` | `EnableDevLogin` | Enable developer login | Entwickler-Anmeldung aktivieren |
| `SignInSpec` | `MicrosoftClientId` | Microsoft client id | Microsoft-Client-ID |
| `SignInSpec` | `MicrosoftTenantId` | Microsoft tenant id — blank means multi-tenant | Microsoft-Mandanten-ID — leer heißt mandantenübergreifend |
| `SignInSpec` | `GoogleClientId` | Google client id — empty turns the scheme off | Google-Client-ID — leer schaltet das Schema ab |
| `SignInSpec` | `LinkedInClientId` | LinkedIn client id — empty turns the scheme off | LinkedIn-Client-ID — leer schaltet das Schema ab |
| `SignInSpec` | `AppleClientId` | Apple client id | Apple-Client-ID |
| `EmailSpec` | `Enabled` | Enable system email | System-E-Mail aktivieren |
| `EmailSpec` | `ClientId` | Sender app client id | Client-ID der Absender-App |
| `EmailSpec` | `TenantId` | Tenant id | Mandanten-ID |
| `EmailSpec` | `MailboxAddress` | Mailbox address | Postfach-Adresse |
| `EmailSpec` | `UseManagedIdentity` | Use managed identity | Verwaltete Identität verwenden |
| `EmailSpec` | `InboundEnabled` | Enable inbound email (Graph subscription on the mailbox) | Eingehende E-Mail aktivieren (Graph-Abonnement auf dem Postfach) |
| `EmailSpec` | `WebhookBaseUrl` | Public webhook base URL (Graph calls {url}/api/email) | Öffentliche Webhook-Basis-URL (Graph ruft {url}/api/email auf) |
| `EmailSpec` | `InboundForwardAddress` | Inbound forward address — empty disables Forward | Weiterleitungsadresse — leer deaktiviert „Weiterleiten“ |
| `GitHubAppIdentity` | `ClientId` | GitHub App client id | GitHub-App-Client-ID |
| `GitHubAppIdentity` | `InstallationId` | Installation id | Installations-ID |
| `GitHubAppIdentity` | `InstallationOwner` | Installation owner | Inhaber der Installation |
| `HostingOperatorSpec` | `Enabled` | Run the hosting operator here (the control instance only) | Hosting-Operator hier ausführen (nur die Kontrollinstanz) |
| `HostingOperatorSpec` | `Namespace` | Operator namespace | Operator-Namespace |
| `HostingOperatorSpec` | `ServiceAccount` | Operator service account | Operator-Dienstkonto |
| `HostingOperatorSpec` | `Image` | Operator image | Operator-Image |
| `HostingOperatorSpec` | `Environment` | Job environment (KEY=VALUE) | Job-Umgebung (KEY=VALUE) |
| `TelemetrySpec` | `OtlpEndpoint` | OTLP endpoint | OTLP-Endpunkt |
| `TelemetrySpec` | `OtlpProtocol` | OTLP protocol | OTLP-Protokoll |
| `DrainSpec` | `DrainSeconds` | Drain ceiling in seconds (termination grace) | Drain-Obergrenze in Sekunden (Beendigungsfrist) |
| `DrainSpec` | `ShutdownMarginSeconds` | Shutdown margin in seconds | Reserve für das Herunterfahren in Sekunden |
| `StartupProbeSpec` | `PeriodSeconds` | Probe period in seconds | Prüfintervall in Sekunden |
| `StartupProbeSpec` | `TimeoutSeconds` | Probe timeout in seconds | Prüf-Timeout in Sekunden |
| `StartupProbeSpec` | `FailureThreshold` | Failures before the pod is killed | Fehlversuche bis der Pod beendet wird |
| `StorageLayout` | `Backend` | Persistence backend | Persistenz-Backend |
| `StorageLayout` | `DataRoot` | Data root | Datenwurzel |
| `StorageLayout` | `ModulesRoot` | Modules root — blank means the data root | Modulwurzel — leer heißt Datenwurzel |
| `StorageLayout` | `ContentPath` | Content path | Inhaltspfad |
| `StorageLayout` | `ContentName` | Content collection name | Name der Inhaltssammlung |
| `StorageLayout` | `ContentSourceType` | Content source type | Typ der Inhaltsquelle |
| `StorageLayout` | `GraphStorageType` | Graph storage type | Typ des Graph-Speichers |
| `StorageLayout` | `GraphStoragePath` | Graph storage path — blank means {DataRoot}/graph | Pfad des Graph-Speichers — leer heißt {DataRoot}/graph |
| `StorageLayout` | `ClaudeCodeConfigDirRoot` | Claude Code config root (per-user) | Claude-Code-Konfigurationswurzel (pro Benutzer) |
| `InlineEnvOverride` | `Key` | Environment variable name, as set on the pod | Name der Umgebungsvariable, wie auf dem Pod gesetzt |
| `InlineEnvOverride` | `Value` | Value — omit for a credential | Wert — bei Anmeldedaten weglassen |
| `InlineEnvOverride` | `IsSecret` | The value is a credential and is not recorded here | Der Wert ist ein Geheimnis und wird hier nicht erfasst |
| `InlineEnvOverride` | `Shadows` | The envFrom source it shadows — blank means it is the only source | Die envFrom-Quelle, die es überdeckt — leer heißt einzige Quelle |
| `InlineEnvOverride` | `AgreesWithShadowed` | Does the shadowed source agree? Unset means not compared (a Secret) | Stimmt die überdeckte Quelle überein? Nicht gesetzt heißt nicht verglichen (ein Secret) |
| `InlineEnvOverride` | `Reason` | Why it is still set inline | Warum es weiterhin inline gesetzt ist |
| `InlineEnvOverride` | `RetiredBy` | What retires it (issue reference) | Was es ablöst (Issue-Referenz) |
| `GateSpec` | `Language` | Language (python, node, pandas) | Sprache (python, node, pandas) |
| `GateSpec` | `Enabled` | Enabled | Aktiviert |
| `GateSpec` | `Image` | Sidecar image — blank means the gate does not run | Sidecar-Image — leer heißt, das Gate läuft nicht |
| `GateSpec` | `Address` | Mesh address — blank means the chart's default | Mesh-Adresse — leer heißt der Standard des Charts |
