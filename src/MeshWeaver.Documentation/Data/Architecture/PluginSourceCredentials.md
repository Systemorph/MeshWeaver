---
Name: Plugin Source Credentials
Category: Architecture
Description: A git plugin source states its own credential by reference — a vault-delivered token (TokenKey) or the GitHub App installation on another account (InstallationOwner) — so one instance reads repositories in several organisations; a declared credential that cannot be produced refuses the repository by name and never falls back.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="7.5" cy="15.5" r="4.5"/><path d="M10.7 12.3 21 2"/><path d="m16 7 3 3"/><path d="m19 4 2 2"/></svg>
---

# Plugin Source Credentials

An instance reads its git [plugin sources](/Doc/Architecture/PluginRegistry)
(`PluginCatalog:Sources:N:RepoPath`) with **one** identity by default: the GitHub App installation
the instance is configured with (`GitHub:App:*`). An installation token reads the repositories of
the account it is installed on and nothing else — so an instance whose App lives on `Systemorph`
could not read a private repository in another organisation, and GitHub answers a private
repository it will not show as *not found*, which reads as a wrong URL rather than a missing
credential.

A source therefore states its **own** credential, by reference. Two forms; state at most one.

| Source key | Means | The token comes from |
|---|---|---|
| `PluginCatalog:Sources:N:TokenKey` | the **configuration key** the repository's token is read from | the vault, delivered to the pod under that key |
| `PluginCatalog:Sources:N:InstallationOwner` | the account (organisation or user) whose installation of the instance's App reads the repository | minted per installation, cached per installation |
| *(neither)* | the instance's own installation | as before — nothing changes for an existing source |

```json
{
  "PluginCatalog": {
    "Sources": [
      { "Name": "Plugins", "RepoPath": "https://github.com/Systemorph/MeshWeaver.Plugins", "Ref": "main" },
      { "Name": "Extensions",     "RepoPath": "https://github.com/Northwind/extensions",             "Ref": "main",
        "TokenKey": "PluginCatalog:SourceTokens:Extensions" },
      { "Name": "Modules", "RepoPath": "https://github.com/Fabrikam/modules",              "Ref": "main",
        "InstallationOwner": "Fabrikam" }
    ]
  }
}
```

## A token by reference — `TokenKey`

`TokenKey` holds a key **name**, never a token. The value arrives under that key from the vault:
`PluginCatalog:SourceTokens:Extensions` is the environment variable `PluginCatalog__SourceTokens__Extensions`,
which the chart's `keyVaultSecrets` mapping fills from a Key Vault object. Either spelling of the
key (`A:B:C` or `A__B__C`) names the same key. The token is whatever GitHub accepts over https for
that repository — a fine-grained personal access token with **Contents: Read** on the one
repository is the narrowest. It is read on every fetch, so a rotated value is picked up when the
pod next starts with it.

On a Deployment record the declaration is one field on the mount:

```json
{ "name": "Extensions", "url": "https://github.com/Northwind/extensions", "ref": "main",
  "isRegistrySource": true, "secretName": "memex-PluginSource-Extensions-Token" }
```

`secretName` is the vault object's **name**. From it the renderers derive both halves, so they
cannot disagree: `pluginCatalog.sources[].tokenKey: PluginCatalog:SourceTokens:Extensions` and the vault
mapping `{vaultSecret: memex-PluginSource-Extensions-Token, key: PluginCatalog__SourceTokens__Extensions}`
(`DeploymentPortalConfig.SourceCredentialSecrets`). The key follows the source's **name**, not its
index — reordering the mounts never moves a token onto another repository. Characters of the name
that are not letters or digits become `_` in the key.

## Another installation — `InstallationOwner`

One GitHub App can be installed on several accounts. `InstallationOwner` names the account, and
`GitHubAppTokenService.GetInstallationToken(owner)` mints that installation's token: one promise
per owner, refreshed five minutes before expiry exactly like the instance's own. The App must be
installed on that account with **Contents: Read** on the repository. On a record:
`"installationOwner": "Fabrikam"` on the mount (fluent: `WithPluginSource(name, url, installationOwner: …)`).

`GitHub:App:InstallationId` pins the instance's *own* installation and says nothing about another
account's, so a named owner is always discovered by name.

## Fail closed, by name

A declared credential that cannot be produced **refuses the repository**. Nothing falls back.

| What is wrong | What happens |
|---|---|
| `TokenKey` is declared and the key is empty or absent | `GitSourceCredentialException` naming the source, the repository and the key (both spellings); no request reaches GitHub |
| `InstallationOwner` is declared and the instance has no App | `GitSourceCredentialException` naming the source and the owner |
| the App has no installation on the named owner | `GitHubAppTokenMintException` (`InstallationDiscovery`) naming the owner — never another installation's token |
| `TokenKey` is present and blank | `GitSourceCredentialException` — a present key is a declaration, not an absence |
| a source states BOTH `TokenKey` and `InstallationOwner` in configuration | `GitSourceCredentialException` naming both — no winner is picked |
| a record names both `secretName` and `installationOwner`, or `installationOwner` on a consumer mount | a spec problem; the mount is not rendered |
| two mounts whose names derive the same token key (`a-b` and `a_b`) | a spec problem naming both; the second is not rendered, so one repository is never handed the other's token |

The refusals are raised server-side with no viewer in scope, so each carries a catalog key and
named arguments (`gitsync.source.tokenKeyEmpty`, `.tokenKeyBlank`, `.appNotConfigured`, `.twoCredentials`,
`.noInstallation`) and is rendered in the viewer's language wherever a sync shows it.

The reason it is not a fallback: the instance's own token reads nothing in the other organisation,
so "try the App instead" turns a missing secret into a 404 on the repository, reported one layer
away from its cause. The chart renders `TokenKey` only when stated for the same reason — an
unconditional empty key would be a *declared* key that is empty.

## One reader, every route

A repository is read on more than one route: the package source that lists and installs it, and
[GitSync](/Doc/Architecture/GitHubSync)'s import and branch reconcile of the Spaces installed out
of it. `GitSourceCredentials` (in `MeshWeaver.GitSync`) is the one reading of the two keys, and it
is keyed by **repository URL** (trailing slash, `.git` and case ignored), so every route gets the
same answer:

- `PackageSources` — the configured list, a `PluginCatalog` node over the same URL, the update watcher;
- `GitHubSyncService` — import, re-import, branch state, branch head. A user's own connected
  GitHub credential still comes first; the declared source credential replaces the instance App
  only when there is none. **The export (a push) does not take it**: a source credential is a
  read credential, so a push keeps the user's or the instance's own identity;
- `GitHubRepoIdentityResolver` — the canonical-name lookup behind webhook matching.

## Related

- [Plugin Registry](/Doc/Architecture/PluginRegistry) — the sources list this page extends.
- [Configuring an Instance from Aspire](/Doc/Architecture/ConfiguringAnInstanceFromAspire) — the record → Helm value → config key table (`WithPluginSource`).
- [Sources Sync on Push](/Doc/Architecture/SourcesSyncOnPush) — how a push to a source's branch reaches the instance.
