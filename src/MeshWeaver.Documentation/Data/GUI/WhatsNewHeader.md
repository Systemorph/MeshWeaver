---
Name: What's New in the header
Category: Documentation
Description: The ✨ entry left of the avatar opens the newest release notes and shows a dot until you have opened them — tracked per user on your own profile, never in the browser
Icon: Sparkle
---

The signed-in header carries a ✨ **What's New** entry, left of the avatar and next to the chat
toggle, on desktop and on mobile. It opens the **newest release-notes page**, the map of what the
latest release brought, and that page links the day-by-day What's New feed under Settings. The
avatar menu keeps its own What's New entry, which opens that feed directly. Anonymous visitors do
not get the entry: their header is the public storefront. Policy
[`whats-new-header`](/Doc/Architecture/PolicyNotProse).

## Which page it opens

Release notes are written per release (policy `whatsnew-cadence`), one page per version under
`Doc/ReleaseNotes`, named by the version with underscores: `3_0_0`, `3_1_0`. The entry lists that
namespace's children and opens the one with the highest version. It never goes by the order rows
arrive in or by string order, so `3_10_0` beats `3_9_0`. A page whose name is not a version never
wins over one that is. If a release is cut without its page, the entry keeps showing the previous
release. That is a release task, described in
[Release Process §6](/Doc/Architecture/ReleaseProcess).

## The dot

While you have not opened the newest release, the glyph carries a dot and its tooltip says so.
Opening it from the header records the page on your own profile, `User.LastSeenReleaseNotes`,
through `GetMeshNodeStream(userPath).Update`. The dot then clears in every tab and on every device.
Browser storage is never the source of truth for it. When the next release's page is published, its
path differs from the one you saw and the dot returns.

Opening the release notes some other way (a link, the documentation tree) does not clear the dot:
only the header entry records the visit.

## Where the pieces live

| Piece | Where |
|---|---|
| Newest-page choice, the seen check, the write | `WhatsNewRelease` in `src/MeshWeaver.Graph` (core) — `NewestPath`, `IsUnseen`, `ObserveWhatsNewRelease`, `MarkReleaseNotesSeen` |
| The per-user field | `User.LastSeenReleaseNotes` (core) |
| The Blazor header button | `WhatsNewHeaderButton` in MeshWeaver.Plugins `src/MeshWeaver.Blazor.Portal` |
| The React header button | `WhatsNewButton` in MeshWeaver.Plugins `clients/portal-next` |
| Tooltip text | catalog keys `header.whatsNew.tooltip` and `header.whatsNew.unseenTooltip` (en and de) |

Tests: `WhatsNewReleaseTest` (`test/MeshWeaver.Graph.Test`) covers the newest-by-version choice,
the link, and the dot clearing once the visit is written.

## See also

- [Release Process & Versioning](/Doc/Architecture/ReleaseProcess)
- [Settings Page](../SettingsPage)
- [The Apps Home](/Doc/Architecture/AppsHome), whose Getting started guide points new users at the documentation
