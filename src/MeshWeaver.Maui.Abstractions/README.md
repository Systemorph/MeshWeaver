# MeshWeaver.Maui.Abstractions

Shared abstractions between the MeshWeaver server and the MAUI mobile client. Declares the
control manifest and projection contracts the server uses to shape layout-area content for
native rendering — without taking any MAUI dependency itself.

## Features

- `MauiControlManifest` — the set of controls the mobile client can render
- `MauiChatProjection` / `MauiItemTemplateProjection` — server-side projections of chat and list content
- `MauiHref`, `MauiComboboxFilter`, `MauiOptionCoercion` — navigation and input shaping helpers

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)
