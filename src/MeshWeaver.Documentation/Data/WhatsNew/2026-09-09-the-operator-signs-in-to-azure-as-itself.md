---
Name: The operator signs in to Azure as itself
Category: Fix
Description: The first new instance provisioned through the control portal stopped at its first step — Azure asked the operator to log in. The job had the identity all along; the CLI just never used it. The job now signs in once, as its workload identity, before the first step, and a run that cannot is refused by name.
Icon: Wrench
Order: -20260909
---

On 2026-09-09 the first `Provision` of a new instance (pearl) was filed through the control
portal, on an operator that had by then made it through every earlier wall. It rendered its
fourteen steps and stopped at the first:

```
[1/14] Create database
ERROR: Please run 'az login' to setup account.
```

## What was happening

The operator runs as a Kubernetes ServiceAccount federated to a managed identity, and the
cluster's workload-identity webhook projects a token into the job and sets the `AZURE_*`
variables for it. The Azure **SDKs** read those on their own; the Azure **CLI** does not. Nothing
in the job ever ran `az login`, so every step that touches Azure — the database, the Key Vault
secrets, the federation, DNS, TLS — had never worked through the lane. No earlier run had reached
one: memex's reconciles are kubectl and helm only, which authenticate in-cluster.

## What it does now

The job signs in once, before the first step, as its identity with the projected token, and logs
`signed in as <client id>`; the token never reaches a command line or the log. A sign-in that
fails stops the run before any step and names what to check — the federated credential's subject
and issuer, which is the one place a mismatch shows. A run with no token at all (a reconcile that
needs no Azure) says so and carries on. The operator's test suite asserts all of it against a
stubbed CLI.
