---
Name: Displaying Times in the Viewer's Zone
Category: Documentation
Description: Stored instants are UTC — render them through AccessService.ToDisplayTime so every reader sees their own wall clock, with named IANA zones so DST follows the region
---

Every timestamp in the mesh is **stored, serialized, versioned, sorted and logged in UTC**. Only
*rendering* converts. This page is the one rule for that conversion, and the traps that make getting
it wrong invisible.

> 🚨 **`.ToString(...)` on a stored timestamp shows UTC to every viewer.** So does `.ToLocalTime()`
> and `.LocalDateTime` — under Blazor Server those resolve to the **server process** zone, and the
> deployment container runs UTC, so the conversion is a no-op that looks like a conversion.

---

# The seam

```
User.TimeZoneId (IANA, on the User node)
  └─► AccessContext.TimeZoneId      (resolved when the context is built)
        └─► AccessService.ToDisplayTime(instant)
```

Because the zone rides on the **identity** rather than on the browser, the same call is correct on
the Blazor circuit *and* on server-side hub render paths that have no browser at all. Conversion is
fully synchronous — safe to call on the render path, no profile lookup, no `async`. Named IANA zones
mean DST is applied automatically and per-region. An unknown viewer, or a viewer whose zone is unset
or invalid, renders **UTC** — the display is never wrong, just un-localized.

```csharp
var access = host.Hub.ServiceProvider.GetService<AccessService>();   // MeshWeaver.Messaging
var when = access.ToDisplayTime(node.LastModified).ToString("yyyy-MM-dd HH:mm");
```

In a Blazor view, `BlazorView` already exposes a protected `AccessService` — just call
`AccessService.ToDisplayTime(...)`.

---

# Capture the zone when the value is formatted LATER

`ToDisplayTime(instant)` — the `AccessService` extension — resolves the `AsyncLocal` access context
**at the moment it is called**. On a synchronous render turn that is exactly right.

It is **silently wrong** when the formatting happens on a *later* emission that has left that scope:
an `IIoPool` HTTP result, a pooled scheduler hop, a change-feed callback. There the context no longer
flows, resolution returns null, and the value degrades to UTC — with no error, no log line, and no
failing test. Two call sites resolving separately can also disagree with each other on the same page.

So on those paths, capture on the render turn and pass the id down:

```csharp
// On the render turn — the context is still ambient here.
var zoneId = host.Hub.ServiceProvider.GetService<AccessService>().ViewerZoneId();

// On a later emission — the id is a plain string, so it cannot degrade.
return prService.ListAll(spacePath, null, userId)
    .Select(rows => rows.Select(p => new Row(
        p.Number,
        DisplayTimeExtensions.ToDisplayTime(p.UpdatedAt, zoneId).ToString("yyyy-MM-dd"))));
```

Rule of thumb: **if the value is formatted anywhere other than the synchronous body of the area,
capture the zone.**

---

# Do NOT convert calendar facts

A *date* is not an instant. A policy inception, a date of loss, a valuation / as-of date, a delivery
date, a certification expiry, an analytics day-bucket key — these are calendar facts. An inception of
`2026-01-01` is `2026-01-01` in Zurich, in New York and in UTC.

Converting one **shifts data and corrupts joins** — a day-bucket key that moves by an hour silently
lands in the wrong bucket, and a report reconciles against nothing.

| Convert | Leave alone |
|---|---|
| created, modified, submitted, decided, published, indexed, last-used, expiry *instant* | inception, date of loss, as-of / valuation date, delivery date, chart day-bucket keys |

The question to ask is not "is this a date type" but **"did this happen at an instant, or on a day?"**

---

# Comparisons use UTC — only the rendered value is a wall clock

A surprisingly common variant of the bug has nothing to do with formatting:

```csharp
// ❌ compares a stored UTC instant against the SERVER's clock — every bucket
//    boundary is off by the host offset.
var age = DateTime.Now - node.LastModified;
```

Measure age, ordering, overdue-ness and "is it in the future" against `UtcNow`. Convert only the
value you are about to render.

---

# Machine-facing output stays UTC, and says so

An agent/MCP tool that emits a timestamp a caller may hand back — a version list feeding a
"restore to this point" call, for instance — must emit **ISO-8601 with the `Z`**. A zone-less string
parses in the *server's* zone on the way back in, so a round trip silently lands on a different
instant. Machine output is UTC; the UI is what localizes.

---

# Native clients are different

MAUI and other native clients keep `.ToLocalTime()`: there the process **is** the device, so the
system zone is the real viewer zone. The seam exists because Blazor Server renders on a shared
container, not because `ToLocalTime` is wrong everywhere.

---

# Testing

Pin **both DST directions** — a fixed-offset implementation passes one and fails the other — plus
the fallback and the date rollover. Keep the formatter pure (take the zone as an argument) so the
test needs no hub and no circuit:

```csharp
public static string DisplayStamp(DateTimeOffset instant, string? zoneId) =>
    DisplayTimeExtensions.ToDisplayTime(instant, zoneId).ToString("yyyy-MM-dd HH:mm");
```

| Stored instant | Zone | Expected | Why this row exists |
|---|---|---|---|
| `2026-07-20 14:32Z` | `Europe/Zurich` | `16:32` | CEST, UTC+2 |
| `2026-01-20 14:32Z` | `Europe/Zurich` | `15:32` | CET, UTC+1 — catches a hard-coded offset |
| `2026-07-20 14:32Z` | `America/New_York` | `10:32` | a second region — "US time" is a specific zone |
| `2026-07-29 23:30Z` | `Europe/Zurich` | **`2026-07-30`** `01:30` | the DATE moves — this is why a missed conversion reads as an off-by-one rather than a timezone bug |
| any | `null` / unknown | unchanged (UTC) | must degrade to UTC, **never** to the server's zone — a server-local fallback looks right in CI and is wrong in Zurich |

---

# Why this has its own page

The seam shipped with 7 render sites converted. An audit two weeks later found **15 more still
formatting the raw value** — the activity header, node Settings ▸ Timestamps, every chat timestamp,
API token expiry, the GitHub issue/PR columns, the file browser — plus several plugin surfaces.

Nothing failed. Nothing was red. The only visible tell was **two surfaces on the same page
disagreeing**: the node header saying `16:04` while the comment beside it said `14:04`.

That is why the rule is "route it through the seam", not "remember to convert".

## See also

- [Access Context Propagation](/Doc/Architecture/AccessContextPropagation) — how the identity, and
  therefore the zone, reaches a render path.
- [Data Binding](/Doc/GUI/DataBinding) — the contract every backend area follows.
