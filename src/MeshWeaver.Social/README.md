# MeshWeaver.Social

Social publishing on the MeshWeaver mesh. Turns mesh content into platform posts through an
approval-gated pipeline, with LinkedIn as the first platform integration.

## Features

- `IPlatformPublisher` / `IPublishQueue` — the platform-agnostic publishing pipeline
- `ApprovalToPublishHandler` — publishing gated on mesh `Approval` nodes
- `LinkedInPublisher` / `LinkedInPublishService` / `LinkedInPostsApi` — publish, list, and manage LinkedIn posts
- `LinkedInAnalytics` + `PastPostIngestJob` — per-post analytics and history import
- `PlatformCredential` — per-user platform credentials as mesh data

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)
