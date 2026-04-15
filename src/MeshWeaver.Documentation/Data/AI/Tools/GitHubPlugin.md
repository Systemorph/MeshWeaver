---
Name: GitHubPlugin Tools
Category: Documentation
Description: Complete reference for GitHubPlugin tools used by AI agents to manage GitHub issues
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/></svg>
---

GitHubPlugin provides tools for managing GitHub issues from AI agents. It authenticates via a Personal Access Token configured through `GitHubConfiguration` or the `GITHUB_TOKEN` environment variable.

## CreateIssue

Creates a new GitHub issue in the specified repository. Returns JSON with `url`, `number`, and `state`.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `owner` | string | No | Repository owner (org or user). Falls back to `DefaultOwner` if omitted. |
| `repo` | string | No | Repository name. Falls back to `DefaultRepo` if omitted. |
| `title` | string | Yes | Issue title |
| `body` | string | Yes | Issue body in Markdown format |
| `labels` | string | No | Comma-separated labels to apply (e.g., `feature-spec,priority:high`) |
| `milestone` | string | No | Milestone name to assign |

### Example

```
CreateIssue(null, null, "Add export feature", "## Summary\nExport data to CSV...", "feature-spec,priority:high")
```

## GetIssue

Gets details of a GitHub issue by number. Returns title, state, body, labels, URL, and timestamps.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `owner` | string | No | Repository owner |
| `repo` | string | No | Repository name |
| `issueNumber` | int | Yes | Issue number |

### Example

```
GetIssue(null, null, 42)
```

## ListIssues

Lists GitHub issues in a repository, filtered by state and labels.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `owner` | string | No | Repository owner |
| `repo` | string | No | Repository name |
| `state` | string | No | Filter by state: `open`, `closed`, or `all` (default: `open`) |
| `labels` | string | No | Comma-separated labels to filter by |
| `perPage` | int | No | Maximum number of issues to return (default: 10, max: 100) |

### Example

```
ListIssues(null, null, "open", "feature-spec", 20)
```

## UpdateIssue

Updates an existing GitHub issue. Only specified fields are changed.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `owner` | string | No | Repository owner |
| `repo` | string | No | Repository name |
| `issueNumber` | int | Yes | Issue number to update |
| `state` | string | No | New state: `open` or `closed` |
| `title` | string | No | New title |
| `body` | string | No | New body |
| `labels` | string | No | Comma-separated labels to set |

### Example

```
UpdateIssue(null, null, 42, "closed")
```

## Configuration

### Registration

Register the plugin in your service configuration:

```csharp
services.AddGitHubPlugin(config =>
{
    config.DefaultOwner = "Systemorph";
    config.DefaultRepo = "MeshWeaver";
});
```

Or register with defaults and rely on environment variables:

```csharp
services.AddGitHubPlugin();
```

### Authentication

The plugin requires a GitHub Personal Access Token (PAT) for issue management.

> **Security recommendation:** Use fine-grained PATs scoped to specific repositories and grant only the "Issues: Read and write" permission. Avoid using classic tokens with the full `repo` scope.

#### Token Resolution Order

1. `GitHubConfiguration.PersonalAccessToken` — from app configuration (e.g., user secrets)
2. `GITHUB_TOKEN` environment variable — automatic fallback

If neither is set, all tools return an error prompting configuration.

#### Monolith (local dev)

Set the token via .NET user secrets:

```bash
dotnet user-secrets set "GitHub:PersonalAccessToken" "github_pat_xxx" --project memex/Memex.Portal.Monolith
```

Or export the `GITHUB_TOKEN` environment variable before running the app:

```bash
export GITHUB_TOKEN="github_pat_xxx"
```

#### Aspire (distributed)

Set the token as an Aspire parameter via user secrets:

```bash
dotnet user-secrets set "Parameters:github-token" "github_pat_xxx" --project memex/aspire/Memex.AppHost
```

The AppHost wires this parameter as the `GitHub__PersonalAccessToken` environment variable to the portal container.

### Agent Frontmatter

To give an agent access to GitHub tools, include `GitHub` in the `plugins` list:

```yaml
---
nodeType: Agent
name: My Agent
plugins:
  - GitHub
---
```
