---
Name: SpecWriter Agent
Category: Documentation
Description: Agent that generates structured specifications from Markdown nodes and publishes them as GitHub issues
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/><line x1="16" y1="13" x2="8" y2="13"/><line x1="16" y1="17" x2="8" y2="17"/><polyline points="10 9 9 9 8 9"/></svg>
---

SpecWriter is an AI agent that reads a Markdown node describing a bug or feature, generates a structured specification, and publishes it as a GitHub issue.

## Plugins

SpecWriter uses two plugins:

| Plugin | Tools | Purpose |
|--------|-------|---------|
| **Mesh** | Get, Search, Create, Update | Read source nodes, find context, write specs back |
| **GitHub** | CreateIssue, GetIssue, ListIssues, UpdateIssue | Publish specs as GitHub issues |

## Delegations

| Agent | Purpose |
|-------|---------|
| **Research** | Gather broader context about related features, existing code patterns, and domain knowledge |

## Usage

Reference the agent in chat:

```
@agent/SpecWriter
```

Or combine with a prompt:

```
@agent/SpecWriter create a spec from @ACME/FeatureIdea
```

## Prerequisites

- The **GitHub plugin** must be configured with a valid Personal Access Token. See [GitHubPlugin Tools](Tools/GitHubPlugin) for configuration details.
- If the GitHub plugin is not configured, SpecWriter will still generate the spec and update the node, but will skip GitHub issue creation and inform the user.
