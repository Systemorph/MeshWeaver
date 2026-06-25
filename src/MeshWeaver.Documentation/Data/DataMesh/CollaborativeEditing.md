---
Name: Collaborative Editing
Category: Documentation
Description: Real-time collaborative markdown editing with comments, track changes, and annotation satellite entities
Icon: /static/DocContent/DataMesh/CollaborativeEditing/icon.svg
---

Work together on documents in real time — comment on passages, propose edits as tracked suggestions, and accept or reject changes without ever leaving the document.

---

## How It Works: Annotations as Satellite Entities

<svg viewBox="0 0 760 360" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arrow" markerWidth="10" markerHeight="7" refX="9" refY="3.5" orient="auto">
      <polygon points="0 0, 10 3.5, 0 7" fill="currentColor" fill-opacity=".55"/>
    </marker>
  </defs>
  <rect x="10" y="10" width="740" height="340" rx="12" fill="none" stroke="currentColor" stroke-opacity=".15" stroke-width="1"/>
  <text x="380" y="32" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="11" font-weight="bold" letter-spacing="1">CLEAN DOCUMENT + POSITION-ANCHORED SATELLITES</text>
  <rect x="300" y="48" width="160" height="56" rx="10" fill="#1e88e5"/>
  <text x="380" y="72" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">Document Node</text>
  <text x="380" y="91" text-anchor="middle" fill="#fff" font-size="11">clean markdown — no markers</text>
  <line x1="200" y1="76" x2="298" y2="76" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arrow)"/>
  <line x1="462" y1="76" x2="558" y2="76" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arrow)"/>
  <rect x="40" y="48" width="160" height="56" rx="10" fill="#5c6bc0"/>
  <text x="120" y="72" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">_Comment</text>
  <text x="120" y="91" text-anchor="middle" fill="#fff" font-size="11">Comment satellite</text>
  <rect x="560" y="48" width="160" height="56" rx="10" fill="#8e24aa"/>
  <text x="640" y="72" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">_Tracking</text>
  <text x="640" y="91" text-anchor="middle" fill="#fff" font-size="11">TrackedChange satellite</text>
  <text x="120" y="124" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="10">captures start, length,</text>
  <text x="120" y="138" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="10">version, anchor text</text>
  <text x="640" y="124" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="10">+ original / suggested text</text>
  <text x="640" y="138" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="10">(insert / delete / replace)</text>
  <line x1="380" y1="104" x2="380" y2="158" stroke="currentColor" stroke-opacity=".35" stroke-width="1.5" marker-end="url(#arrow)"/>
  <text x="392" y="135" fill="currentColor" fill-opacity=".45" font-size="10">at render</text>
  <rect x="40" y="162" width="170" height="52" rx="8" fill="#26a69a"/>
  <text x="125" y="184" text-anchor="middle" fill="#fff" font-weight="bold">anchor text @ v3</text>
  <text x="125" y="202" text-anchor="middle" fill="#fff" font-size="11">the text when captured</text>
  <rect x="232" y="162" width="170" height="52" rx="8" fill="#43a047"/>
  <text x="317" y="184" text-anchor="middle" fill="#fff" font-weight="bold">current text @ v7</text>
  <text x="317" y="202" text-anchor="middle" fill="#fff" font-size="11">the document now</text>
  <rect x="424" y="162" width="150" height="52" rx="8" fill="#f57c00"/>
  <text x="499" y="184" text-anchor="middle" fill="#fff" font-weight="bold">diff (version delta)</text>
  <text x="499" y="202" text-anchor="middle" fill="#fff" font-size="11">map the offsets</text>
  <rect x="596" y="162" width="124" height="52" rx="8" fill="#e53935"/>
  <text x="658" y="184" text-anchor="middle" fill="#fff" font-weight="bold">effective range</text>
  <text x="658" y="202" text-anchor="middle" fill="#fff" font-size="11">in the live text</text>
  <line x1="210" y1="188" x2="230" y2="188" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arrow)"/>
  <line x1="402" y1="188" x2="422" y2="188" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arrow)"/>
  <line x1="574" y1="188" x2="594" y2="188" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arrow)"/>
  <text x="380" y="252" text-anchor="middle" fill="currentColor" fill-opacity=".55" font-size="11">The highlight (comment) or the inline diff (change) is rendered at the effective range —</text>
  <text x="380" y="270" text-anchor="middle" fill="currentColor" fill-opacity=".55" font-size="11">a transient overlay for that one render. The stored document is never modified.</text>
  <text x="380" y="300" text-anchor="middle" fill="currentColor" fill-opacity=".45" font-size="10">Accepting a change applies its text to the document; rejecting just drops the satellite.</text>
</svg>
*The document text stays clean. Each annotation captures the character range it covers (plus the document version and the text at that version); the live highlight or diff is recomputed at render time.*

MeshWeaver stores annotations **beside** the document, never inside it. Every comment or tracked change is a satellite entity living in a dedicated partition next to the document node:

| Annotation type | Partition | Extension class |
|---|---|---|
| Comment | `_Comment` | `CommentsExtensions` |
| Tracked change | `_Tracking` | `AnnotationExtensions` |

The document's markdown is kept **clean** — nothing is woven into it. Each annotation records, on the satellite, the character range it covers (`Start`/`Length`), the document **version** that range was captured against, and the document **text** at that version (the *anchor*). The inline highlight or diff is never persisted in the document; it is re-derived every time the document is rendered.

### Capturing and recomputing positions

There is no "strip markers / reassemble" round-trip and no marker is ever written into the source. Instead:

1. **Capture** — when you comment or suggest, the satellite records `Start`, `Length`, `Version`, and `AnchorText` (the clean document text at that version) — plus the highlighted/affected text.
2. **Recompute** — when the document is displayed, each annotation's **effective range** is computed against the current text. If the document is still at the captured version, the stored offsets are used directly; if it has moved on, the engine **diffs** the anchor text against the current text and maps the offsets through that diff (a `diff_xIndex`-style position map). This is exposed as `EffectiveStart` / `EffectiveEnd` / `EffectiveVersion`.
3. **Overlay** — the comment highlight, or the tracked-change diff, is injected as a transient span for that render only.

Because the range is recomputed from the actual edit delta, an annotation follows its text when content is inserted or deleted above it — without the document ever carrying annotation state.

### Annotation entity reference

**Comment** (`_Comment` partition)

| Field | Purpose |
|---|---|
| `Start` / `Length` | The captured character range in the document's clean text |
| `Version` / `AnchorText` | The document version + text the range was captured against |
| `EffectiveStart` / `EffectiveEnd` | The range recomputed for the current text (not persisted) |
| `HighlightedText` | The originally selected text |
| `Status` | `Active` or `Resolved` |
| `PrimaryNodePath` | Document path used for permission delegation |

**TrackedChange** (`_Tracking` partition)

| Field | Purpose |
|---|---|
| `ChangeType` | `Insertion`, `Deletion`, or `Replacement` |
| `Start` / `Length` / `Version` / `AnchorText` | The captured range + anchor (as above) |
| `OriginalText` / `NewText` | The text being removed/replaced, and the suggested text |
| `Status` | `Pending`, `Accepted`, or `Rejected` |
| `PrimaryNodePath` | Document path used for permission delegation |

Both types have `IsSatelliteType = true`.

---

## Adding Comments

Select any passage and click **Comment**. A `Comment` satellite is created that captures the selected range, the document version, and the anchor text — the document itself is untouched, so commenting works even without edit access. The highlight is rendered inline from the satellite.

> Comments without a selected range attach to the bottom of the page.

### Example — a paragraph with comments

> MeshWeaver is a powerful platform for building collaborative applications. It provides real-time synchronization and conflict-free editing.

A reviewer might attach comments to:

- "powerful platform" — flag for more specific metrics
- "collaborative applications" — request examples
- "conflict-free editing" — ask about the underlying technology

---

## Making Suggestions (Track Changes)

**Suggest Edit** lets you propose changes without altering the document directly. A `TrackedChange` satellite is created (an insertion, deletion, or replacement); reviewers can accept or reject each suggestion individually — or all at once. The suggestion is shown as an inline **diff** computed from the satellite.

### Suggested additions

Proposed new text shows as a green-underlined insertion.

> The quarterly report shows significant growth of 25% in user engagement.

### Suggested deletions

Text proposed for removal shows struck through. The original text stays visible until the suggestion is decided.

> Please review the outdated documentation before the meeting.

### Combined example

> Our team has completed the comprehensive analysis of the market trends. We recommend immediate action on the following priorities:
>
> 1. Expand into European markets
> 2. Reallocate marketing spend to digital channels
> 3. Improve customer retention rates

---

## Reviewing Changes

### Accepting a change

Click the **checkmark** next to a suggestion — the suggested text is applied to the document at the change's current effective range, and the satellite is dropped.

- **Accept insertion** — the suggested text is inserted into the document.
- **Accept deletion** — the marked text is removed.
- **Accept replacement** — the old text is swapped for the new.

### Rejecting a change

Click the **X** next to a suggestion — the satellite is dropped and the document is left exactly as it was.

### Bulk review

Use **Accept All** / **Reject All** to resolve every pending suggestion in one step.

---

## Position Tracking Under Edits

When the document is edited above or around an annotation, its highlight follows the text — without any stored position drifting, because positions are recomputed from the edit delta:

1. Each annotation captured `Start`/`Length` against a known `Version` and `AnchorText`.
2. At display, if the document has advanced past that version, the engine diffs `AnchorText` against the current text.
3. Offsets **before** an edit map unchanged; offsets **after** shift by the net length delta; an edit **inside** the range grows or shrinks it; if the anchored text is gone the annotation is dropped from the inline view.
4. The result is the `EffectiveStart`/`EffectiveEnd` used for that render.

This is a pure, deterministic text operation — the same engine drives both comment highlights and the tracked-change diff, and it is covered by an extensive unit-test suite.

---

## Working with Multiple Collaborators

Multiple editors work on the same document without conflicts:

- Each collaborator's suggestions are **colour-coded** by author.
- Comments show the **author name and timestamp**.
- Annotation satellites update **reactively** for every connected editor.

### Example — team review session

> **Project Proposal** *(3 collaborators editing)*
>
> The proposed timeline for Phase 1 is 4 months. This aggressive but achievable schedule requires:
>
> - Additional resources from the engineering team
> - Daily standup meetings
> - A dedicated project manager

---

## Tips for Effective Collaboration

1. **Comment before you change** — if you are uncertain, ask rather than edit.
2. **Keep suggestions atomic** — one logical change per suggestion makes review faster.
3. **Resolve threads when done** — mark comment threads resolved to keep the sidebar clean.
4. **Read before bulk-accepting** — scan all pending suggestions before using Accept All.
5. **Add context** — a brief note explaining *why* you propose a change helps reviewers decide quickly.
