---
Name: Collaborative Editing
Category: Documentation
Description: Real-time collaborative markdown editing with comments, track changes, and annotation satellite entities
Icon: /static/DocContent/DataMesh/CollaborativeEditing/icon.svg
---

Work together on documents in real time — comment on passages, edit freely, and see every change tracked (who, when, what) with a one-click revert, without ever leaving the document.

---

## How It Works: Annotations as Satellite Entities

<svg viewBox="0 0 760 360" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arrow" markerWidth="10" markerHeight="7" refX="9" refY="3.5" orient="auto">
      <polygon points="0 0, 10 3.5, 0 7" fill="currentColor" fill-opacity=".55"/>
    </marker>
  </defs>
  <rect x="10" y="10" width="740" height="340" rx="12" fill="none" stroke="currentColor" stroke-opacity=".15" stroke-width="1"/>
  <text x="380" y="32" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="11" font-weight="bold" letter-spacing="1">CLEAN DOCUMENT + ANCHORED COMMENTS + DERIVED CHANGES</text>
  <rect x="300" y="48" width="160" height="56" rx="10" fill="#1e88e5"/>
  <text x="380" y="72" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">Document Node</text>
  <text x="380" y="91" text-anchor="middle" fill="#fff" font-size="11">clean markdown — no markers</text>
  <line x1="200" y1="76" x2="298" y2="76" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arrow)"/>
  <line x1="462" y1="76" x2="558" y2="76" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arrow)"/>
  <rect x="40" y="48" width="160" height="56" rx="10" fill="#5c6bc0"/>
  <text x="120" y="72" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">_Comment</text>
  <text x="120" y="91" text-anchor="middle" fill="#fff" font-size="11">Comment satellite</text>
  <rect x="560" y="48" width="160" height="56" rx="10" fill="#8e24aa"/>
  <text x="640" y="72" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">Version history</text>
  <text x="640" y="91" text-anchor="middle" fill="#fff" font-size="11">every version, author, time</text>
  <text x="120" y="124" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="10">captures start, length,</text>
  <text x="120" y="138" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="10">version, anchor text</text>
  <text x="640" y="124" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="10">tracked changes are DERIVED</text>
  <text x="640" y="138" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="10">from it — nothing is stored</text>
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
  <text x="380" y="300" text-anchor="middle" fill="currentColor" fill-opacity=".45" font-size="10">Reverting a change is a normal versioned write — it lands in the history like any other edit.</text>
</svg>
*The document text stays clean. Comments capture the character range they cover; tracked changes are computed from the version history. Both are recomputed at render time.*

MeshWeaver keeps the document's markdown **clean** — nothing is woven into it. The two annotation kinds get there differently, and the difference is the point:

| Annotation type | Where it lives | Source of truth |
|---|---|---|
| Comment | `_Comment` satellite | The satellite (genuinely additional data — the document never carried it) |
| Tracked change | **nowhere** — a view model | The node's **version history** (`IVersionQuery` / `mesh_node_history`) |

A **comment** records, on its satellite, the character range it covers (`Start`/`Length`), the document **version** that range was captured against, and the document **text** at that version (the *anchor*).

A **tracked change** stores nothing at all. The version history is already the authoritative record of every change — who made it, when, and the full text before and after — so `ChangeProjection` diffs a baseline version against the current text and attributes each resulting hunk to the version step that introduced it. Persisting that a second time only bought a failure class: anchors going stale as the document moved, orphaned satellite state, and two answers to "what changed".

> **Legacy `_Tracking` satellites.** Older builds persisted tracked changes at `{doc}/_Tracking/{id}`. Nothing writes them any more; the node type and the `_Tracking → annotations` table mapping stay registered for a deprecation window so existing rows remain readable.

### Capturing and recomputing positions

There is no "strip markers / reassemble" round-trip and no marker is ever written into the source. Instead:

1. **Capture** — when you comment, the satellite records `Start`, `Length`, `Version`, and `AnchorText` (the clean document text at that version) plus the highlighted text. A tracked change captures nothing: it is *projected* from the history, and the text it was projected against becomes its anchor.
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

**TrackedChange** — a **view model**, computed by `ChangeProjection`, never persisted

| Field | Purpose |
|---|---|
| `ChangeType` | `Insertion`, `Deletion`, or `Replacement` — classified from the diff hunk |
| `Author` / `CreatedAt` / `Version` | The version-history step that introduced the hunk |
| `OriginalText` / `NewText` | What the range held before, and what it holds now |
| `Start` / `Length` / `AnchorText` | The range in the text it was projected against (so a later edit re-locates it) |
| `PrimaryNodePath` | The document the change belongs to |

`Comment` has `IsSatelliteType = true`; `TrackedChange` has no node type of its own any more (the legacy one stays registered read-only — see above).

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

**Suggest Edit** (in the UI, or the agent tool of the same name) applies the edit to the document as a **normal versioned write**. There is no pending-proposal limbo: the edit lands, the version history records who made it and when, and every reader sees it as a tracked change with a one-click **Revert**. Reverting is itself a versioned write, so the whole review is auditable instead of a satellite quietly appearing and disappearing.

### Additions

New text shows as a green-underlined insertion in the redline.

> The quarterly report shows significant growth of 25% in user engagement.

### Deletions

Removed text shows struck through, reconstructed from the baseline version — the current document does not carry it.

> Please review the outdated documentation before the meeting.

### Combined example

> Our team has completed the comprehensive analysis of the market trends. We recommend immediate action on the following priorities:
>
> 1. Expand into European markets
> 2. Reallocate marketing spend to digital channels
> 3. Improve customer retention rates

---

## Reviewing Changes

### Keeping a change

Do nothing. The change is already in the document — that is precisely why it appears in the version history and therefore in the redline. There is no "accept" button because there is nothing left to apply.

### Reverting a change

Click **↩** on the change card. The range is re-resolved against the **live** document (so a concurrent edit can never make the revert splice the wrong text) and the previous text is put back:

- **Revert an insertion** — the added text is removed again.
- **Revert a deletion** — the removed text comes back.
- **Revert a replacement** — the old text is restored.

The revert is a normal versioned write, so it shows up in the history exactly like the edit it undoes.

### Reverting everything

Use the **Versions** page: pick the version you want and restore it. That is one write with one clear meaning, instead of N independent reverts racing each other.

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

- Each change card names the **author** and **when** — read straight off the version that introduced it.
- Comments show the **author name and timestamp**.
- Comment satellites and the derived change list update **reactively** for every connected editor.

> An edit whose text several people touched attributes to nobody rather than to the wrong person — the card then reads as an unattributed change. That is deliberate: guessing an author is worse than admitting the edit is shared.

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
2. **Keep edits atomic** — one logical change per write makes both the redline and the revert clean.
3. **Resolve threads when done** — mark comment threads resolved to keep the sidebar clean.
4. **Reach for Versions for a wholesale undo** — restoring a version beats reverting a dozen cards.
5. **Add context** — a comment explaining *why* you made a change helps reviewers decide quickly.
