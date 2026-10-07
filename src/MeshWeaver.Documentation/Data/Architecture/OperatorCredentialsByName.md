---
Name: Operator Credentials Travel by Name
Category: Architecture
Description: The hosting operator's only environment is a ConfigMap, so a step that needs a secret is handed the Key Vault object's NAME and reads the value itself with the identity it already runs as. Why every database action failed at step 1 for weeks (Memex#132), why the CSI class built for it was consumed by nobody, and the one contract the three Postgres steps now share with hosting-kv-ensure — on the Job executor and the aks-ops lane alike.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="10" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/><path d="M12 15v3"/></svg>
---

# Operator Credentials Travel by Name

**A hosting-operator step is never handed a secret value. It is handed the Key Vault object's
NAME, and it reads the value itself with the identity it already runs as.** That is the one rule;
the rest of this page is why it is the rule and what enforces it.

## The channel a step has, and what it can carry

A plan step — `hosting-backup`, `hosting-kv-ensure`, any of the `hosting-*` commands — runs on one
of two executors, and both give it exactly one environment:

| executor | where the environment comes from | what it is |
|---|---|---|
| the operator **Job** (`memex-ops`) | the control record's `operator.environment`, rendered by the chart into the portal's ConfigMap as `Hosting__Operator__Environment__*` and copied into the Job spec | a ConfigMap — plaintext, readable by anything with configmap read in the namespace |
| the **aks-ops lane** (`aks-ops.yml` → `node-repo-aks-ops.yml`) | the same map, carried inside the signed bundle as a `workflow_dispatch` input and re-exported by `aks-ops-unpack.py` | a workflow input — plaintext in the dispatch payload and the run's inputs |

Neither is a secret channel, and neither can become one without a second mechanism. Both are the
right channel for a **name**: a resource group, an ingress IP, a vault object's name.

## What went wrong (Memex#132)

`hosting-backup`, `hosting-restore` and `hosting-verify-restore` opened with

```
hosting::need_env PGPASSWORD "the admin password for the flexible server (mounted from Key Vault)"
```

and `need_env`'s refusal ended *"It is supplied by Hosting:Operator:Environment on the control
instance"*. So the first real `Backup` that reached the operator failed at step 1 — and the sentence
it failed with named a channel that cannot carry the value, because "supplying it the documented
way" meant putting the flexible server's `memexadmin` password (the one credential that reaches
every instance database on the server) into a ConfigMap. Every `Backup`, `Restore`, `Suspend` and
`Teardown`-with-backup was blocked on that for weeks, on both executors.

The remedy that was built first was a secret channel: `hosting-operator-secrets.yaml` renders a
SecretProviderClass and a CSI-synced Secret into the operator's namespace and publishes three
coordinates so a Job could mount it and read the value from a file. It was consumed by nobody —
nothing opted in, `HostingOperator.Resolve` never attached the class to a Job, and the scripts kept
demanding the env var. Measured on the thread three times over two weeks: the chart half existed,
the wiring half did not, and a values-only "opt-in" would have rendered a class no pod mounts, which
syncs nothing, silently. And when the executor decision (P3a, the aks-ops lane replacing the Job)
was checked, the lane had the same hole with no class to opt into at all.

## The fix: the same read hosting-kv-ensure already makes

`hosting-kv-ensure` — two steps earlier in every Provision — already reads that exact object, by
name, with that exact identity: a VALUE read of `--db-password-secret` in `--vault`, captured into a
variable and never printed.

> 🚨 That value read is one of the four the secrets migration removes: under policy
> `secrets-write-only-entry` the identity that writes the vault may never read a value
> ([Secrets: Write-Only Entry, Split Identities](../SecretsWriteOnlyEntry) → *Where the fleet stands*).
> Until the connection string is composed without reading the admin password back, these steps keep
> running as `hosting-operator`, not as the writer.

The plan hands it `--vault {record.keyVault}` and `--db-password-secret "$AZ_POSTGRES_PASSWORD_SECRET"`
(a name the record's `operator.environment` carries, correctly, in the plaintext channel), and it
composes the instance's connection string from what it reads. So the identity holds the grant, the
name is already delivered, and the read is already trusted. The three Postgres steps now do the same
thing through one shared primitive in `_common.sh`:

```
hosting::pg_password <vault> <object>     # sets and exports PGPASSWORD for this process
```

- **Both flags, or neither.** `--vault V --password-secret O` reads the value; with neither, a
  `PGPASSWORD` already in the environment is honoured (the by-hand shape: an operator at a shell), and
  its absence is refused naming the two flags and the record key that supplies the object name —
  never the ConfigMap.
- **Three answers, not two.** ABSENT (`SecretNotFound`, or an EMPTY value) and REFUSED (`Forbidden`)
  are different sentences, through `hosting::probe`: a vault that refused this identity has ruled
  nothing out about the object ([Denied Is Not Absent](../DeniedIsNotAbsent)). An empty value is
  refused as loudly as an unreadable one — `psql` with an empty `PGPASSWORD` prompts, and a Job that
  prompts hangs to its deadline saying nothing that names the step.
- **The value never leaves the process.** `az` writes it to stdout, which is captured; it reaches
  `pg_dump`/`psql`/`pg_restore` through their environment, never an argv; nothing prints it. Both
  names are validated as plain identifiers before they reach a command line, at the top of each
  script, before anything is reported — the same boundary every other name crosses.
- **A dry run reads nothing.** The rehearsal reads no secret, as `hosting-kv-ensure`'s does not.

It works on **both executors** because both run the same scripts under the same identity: the Job as
the `hosting-operator` workload identity (`run.sh`'s `az login --federated-token`), the lane as the
`hosting-operator` OIDC session (`aks-ops-session.sh`, which hands `run.sh` the same token file).
No class, no mount, no opt-in, no lane step, and nothing to decide between the two executors.

## What holds it

`deploy/aks/operator/test/run-tests.sh` runs the three scripts against stub `az`, `pg_dump`, `psql`
and `pg_restore` that record argv and whether `PGPASSWORD` reached them (as `set`/`unset`, never the
value). A case on each side: the read happens by name and BEFORE `pg_dump`; an absent object, an
empty object, a refusing vault, no channel at all, and one flag without the other each refuse before
anything is dumped, saying which; a hand-set `PGPASSWORD` with no flags is honoured without a vault
read; a dry run reads nothing; a metacharacter in either name is refused before the script reports
anything. The control on the other side is the pre-change script, which refused `--vault` as an
unknown argument and demanded the env var.

## The two halves that are not in this repository

| half | where | state |
|---|---|---|
| the **plan** passes `--vault {vault} --password-secret "$AZ_POSTGRES_PASSWORD_SECRET"` on the six `hosting-backup` / `hosting-restore` / `hosting-verify-restore` command lines it composes | MeshWeaver.Plugins `Hosting/InstanceAction/Source/InstanceActionPlan.cs` — an in-mesh node; no `dotnet build` type-checks it | lands second; until it does the steps fail at step 1 as before, with a refusal that names the flags |
| the **record** carries `AZ_POSTGRES_PASSWORD_SECRET` in `operator.environment` and a `keyVault` | Systemorph/Memex `mesh/Deployments/<id>.json` (+ the rendered overlay) | already true for `memex`; a record with no `keyVault` cannot run a database action and the plan refuses it by name |

Two facts only an operator can verify, and this page does not assert: that the object the record
names actually holds `memexadmin`'s password (a Provision that composed a connection string proved
it once; the record naming it is not that proof), and that the operator identity holds secret `get`
on it in the vault the record names (the same Provision exercised it).

## The chart's `secretEnvironment` block, after this

`hostingOperator.secretEnvironment` and `hosting-operator-secrets.yaml` remain in the chart for a
credential a script cannot fetch by name itself. Nothing in the fleet declares a key there, and no
`HostingOperator.Resolve` attaches the class to a Job, so a key declared today renders a class no pod
mounts. Whether that block stays or goes is a separate decision; what this page settles is that the
Postgres admin password is not what it is for.

## The other direction: a rendered manifest is data, never log text (Memex#626)

Names flow IN. Values must not flow OUT either, and the way out is the step's own output, which
becomes the job log. The mesh copies that log onto the action node, so every reader of
`Ops/Actions` can read it. A rendered manifest carries every value the record and the Key Vault
half render: Secret payloads, connection strings and, on a hand-applied object, whatever a person
put in an env var. Memex#626 is that leak. An ApplyChart dry-run diff printed a live Deployment's
env, an Azure Storage SAS included, into the run log and the node's `callbackLogTail`.

So the rule holds in both repositories:

- **`hosting-deploy`** captures `helm template` and the `kubectl create --dry-run` name listing
  with stdout and stderr APART, and a failure names ONE line of the tool's stderr. It used to
  capture with `2>&1`, which folded whatever the tool had already written to stdout into the
  die line. `test/run-tests.sh` ("the rendered manifest never reaches the log") feeds a render
  carrying a fake SAS through a failing render, a failing rehearsal render and a failing parse.
  Its negative control fails all three cases against the old capture.
- **The ApplyChart diff** (Memex `aks-ops-chart.py`) reads both manifests as objects and redacts
  them structurally, default-deny: a value prints only when it is proven harmless, and everything
  else prints as a per-run keyed fingerprint.
- **The sink** (MeshWeaver.Plugins `CredentialMask`) masks credential-shaped text in every log it
  copies onto a node. It is a regex, so it is the second line of defence, not the first.

## See also

- [Denied Is Not Absent](../DeniedIsNotAbsent) — the three answers a read owes its reader, which
  `hosting::pg_password` gives.
- [Operating from the portal, not the cluster](../OperatingFromThePortal) — the actions these steps
  run under.
- [In-cluster databases](../InClusterDatabases) — the database shape these steps do not yet serve.
