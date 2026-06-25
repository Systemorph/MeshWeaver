---
Name: Collaborative Editing
Category: Documentation
Description: Real-time collaborative markdown editing with comments, track changes, and annotation satellite entities
Icon: /static/DocContent/DataMesh/CollaborativeEditing/icon.svg
---

Work together on documents in real time — comment on passages, propose edits as tracked suggestions, and accept or reject changes without ever leaving the document.

---

## How It Works: Annotations as Satellite Entities
<svg viewBox="0 0 760 340" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arrow" markerWidth="10" markerHeight="7" refX="9" refY="3.5" orient="auto">
      <polygon points="0 0, 10 3.5, 0 7" fill="currentColor" fill-opacity=".55"/>
    </marker>
  </defs>
  <rect x="10" y="10" width="740" height="320" rx="12" fill="none" stroke="currentColor" stroke-opacity=".15" stroke-width="1"/>
  <text x="380" y="32" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="11" font-weight="bold" letter-spacing="1">ANNOTATION ARCHITECTURE &amp; LOAD–EDIT–SAVE PIPELINE</text>
  <rect x="300" y="48" width="160" height="56" rx="10" fill="#1e88e5"/>
  <text x="380" y="72" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">Document Node</text>
  <text x="380" y="91" text-anchor="middle" fill="#fff" font-size="11">(Markdown + inline markers)</text>
  <line x1="200" y1="76" x2="298" y2="76" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arrow)"/>
  <line x1="462" y1="76" x2="558" y2="76" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arrow)"/>
  <rect x="40" y="48" width="160" height="56" rx="10" fill="#5c6bc0"/>
  <text x="120" y="72" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">_Comment</text>
  <text x="120" y="91" text-anchor="middle" fill="#fff" font-size="11">Comment satellite</text>
  <rect x="560" y="48" width="160" height="56" rx="10" fill="#8e24aa"/>
  <text x="640" y="72" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">_Tracking</text>
  <text x="640" y="91" text-anchor="middle" fill="#fff" font-size="11">TrackedChange satellite</text>
  <line x1="120" y1="104" x2="120" y2="144" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" stroke-dasharray="5,3"/>
  <line x1="640" y1="104" x2="640" y2="144" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" stroke-dasharray="5,3"/>
  <text x="120" y="138" text-anchor="middle" fill="currentColor" fill-opacity=".45" font-size="10">MarkerId links to marker</text>
  <text x="640" y="138" text-anchor="middle" fill="currentColor" fill-opacity=".45" font-size="10">MarkerId links to marker</text>
  <line x1="380" y1="104" x2="380" y2="164" stroke="currentColor" stroke-opacity=".35" stroke-width="1.5" marker-end="url(#arrow)"/>
  <text x="395" y="138" fill="currentColor" fill-opacity=".45" font-size="10">pipeline</text>
  <rect x="42" y="168" width="155" height="52" rx="8" fill="#26a69a"/>
  <text x="120" y="190" text-anchor="middle" fill="#fff" font-weight="bold">1. Load</text>
  <text x="120" y="208" text-anchor="middle" fill="#fff" font-size="11">Read markdown + markers</text>
  <rect x="222" y="168" width="155" height="52" rx="8" fill="#43a047"/>
  <text x="300" y="190" text-anchor="middle" fill="#fff" font-weight="bold">2. Separate</text>
  <text x="300" y="208" text-anchor="middle" fill="#fff" font-size="11">Strip markers → clean text</text>
  <rect x="402" y="168" width="155" height="52" rx="8" fill="#f57c00"/>
  <text x="480" y="190" text-anchor="middle" fill="#fff" font-weight="bold">3. Edit</text>
  <text x="480" y="208" text-anchor="middle" fill="#fff" font-size="11">User edits; overlays render</text>
  <rect x="582" y="168" width="155" height="52" rx="8" fill="#e53935"/>
  <text x="660" y="190" text-anchor="middle" fill="#fff" font-weight="bold">4. Save</text>
  <text x="660" y="208" text-anchor="middle" fill="#fff" font-size="11">Shift positions + Reassemble</text>
  <line x1="197" y1="194" x2="220" y2="194" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arrow)"/>
  <line x1="377" y1="194" x2="400" y2="194" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arrow)"/>
  <line x1="557" y1="194" x2="580" y2="194" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arrow)"/>
  <text x="120" y="254" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="11">storage</text>
  <text x="300" y="254" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="11">AnnotationSyncService</text>
  <text x="480" y="254" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="11">auto-save 500 ms throttle</text>
  <text x="660" y="254" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="11">ComputePositionShifts()</text>
  <line x1="660" y1="220" x2="660" y2="276" stroke="currentColor" stroke-opacity=".3" stroke-width="1.2" stroke-dasharray="4,3"/>
  <line x1="660" y1="276" x2="42" y2="276" stroke="currentColor" stroke-opacity=".3" stroke-width="1.2" stroke-dasharray="4,3" marker-end="url(#arrow)"/>
  <text x="380" y="293" text-anchor="middle" fill="currentColor" fill-opacity=".4" font-size="10">markers re-injected → saved back to Document Node</text>
  <line x1="42" y1="276" x2="42" y2="220" stroke="currentColor" stroke-opacity=".3" stroke-width="1.2" stroke-dasharray="4,3"/>
</svg>
*Annotation satellite entities live beside the document; the four-step pipeline separates markers from editable text and reassembles them on save.*

MeshWeaver stores annotations **beside** the document, not inside it. Every comment or tracked change is a satellite entity living in a dedicated partition next to the document node:

| Annotation type | Partition | Extension class |
|---|---|---|
| Comment | `_Comment` | `CommentsExtensions` |
| Tracked change | `_Tracking` | `AnnotationExtensions` |

The markdown source itself holds lightweight **inline markers** (`text`) as the authoritative record of which text range an annotation covers. Positions are always *derived* from those markers at load time — they are never stored on the entities.

### The load–edit–save cycle

Every time a document is opened or saved, the system runs a three-step pipeline:

1. **Load** — read markdown including inline markers.
2. **Separate** — `AnnotationSyncService.Separate()` strips the markers to produce clean editable text plus ephemeral position ranges.
3. **Edit** — the user edits clean text; annotations render as overlays without cluttering the editor.
4. **Save** — `ComputePositionShifts()` detects the edit zone, shifts annotation positions accordingly, and `Reassemble()` re-injects markers before writing back. This runs inside the existing 500 ms auto-save throttle.

### Annotation entity reference

**Comment** (`_Comment` partition)

| Field | Purpose |
|---|---|
| `MarkerId` | Links to the inline marker in the markdown source |
| `HighlightedText` | The originally selected text |
| `Status` | `Active` or `Resolved` |
| `PrimaryNodePath` | Document path used for permission delegation |

**TrackedChange** (`_Tracking` partition)

| Field | Purpose |
|---|---|
| `ChangeType` | `Insertion` or `Deletion` |
| `Status` | `Pending`, `Accepted`, or `Rejected` |
| `PrimaryNodePath` | Document path used for permission delegation |

Both types have `IsSatelliteType = true`.

---

## Adding Comments

Select any passage and click **Comment** in the toolbar. The system creates a `Comment` entity whose `MarkerId` links it to the inline marker that now wraps your selection.

> Comments without a selected range attach to the bottom of the page.

### Example — a paragraph with comments

> MeshWeaver is a powerful platform for building collaborative applications. It provides real-time synchronization and conflict-free editing.

In the above example three comment markers are embedded in the source:

- `c1` — "powerful platform" flagged for more specific metrics
- `c2` — "collaborative applications" tagged with a request for examples
- `c3` — "conflict-free editing" questioned about the underlying technology

---

## Making Suggestions (Track Changes)

**Suggest Edit** lets you propose changes without altering the document directly. A `TrackedChange` entity is created; reviewers can accept or reject each suggestion individually — or all at once.

### Suggested additions

Proposed new text gets a green underline.

> The quarterly report shows significant growth of 25% in user engagement.

### Suggested deletions

Text proposed for removal gets a . The original text stays visible until the suggestion is decided.

> Please review the  documentation before the meeting.

### Combined example

> Our team has completed the comprehensive analysis of the market trends. We recommend immediate action on the following priorities:
>
> 1. Expand into European markets
> 2. Reallocate marketing spend to digital channels
> 3. Improve customer retention rates

---

## Reviewing Changes

### Accepting a change

Click the **checkmark** next to a suggestion:

- **Accept insertion** — the suggested text becomes permanent; `TrackedChange.Status` → `Accepted`.
- **Accept deletion** — the marked text is removed; `TrackedChange.Status` → `Accepted`.

### Rejecting a change

Click the **X** next to a suggestion:

- **Reject insertion** — the suggested text is discarded; `TrackedChange.Status` → `Rejected`.
- **Reject deletion** — the original text is restored; `TrackedChange.Status` → `Rejected`.

### Bulk review

Use the toolbar **Accept All** and **Reject All** buttons to resolve every pending suggestion in one step.

---

## Position Tracking Under Edits

When a user types between two annotations, MeshWeaver automatically recomputes all positions so nothing drifts:

1. `AnnotationSyncService.Separate()` parses markers into clean text and ephemeral position ranges on load.
2. `ComputePositionShifts()` detects the **edit zone** by diffing the old and new clean text.
3. Annotations **before** the edit zone keep their positions unchanged.
4. Annotations **after** the edit zone shift by the content-length delta.
5. Annotations **within** the edit zone are clamped to the nearest boundary.
6. `Reassemble()` re-injects markers at their new positions before saving.

Because positions are always re-derived from markers, there is no stored position state to go stale.

---

## Working with Multiple Collaborators

Multiple editors work on the same document without conflicts:

- Each collaborator's suggestions are **colour-coded** by author.
- Comments show the **author name and timestamp**.
- Changes sync automatically within the auto-save window.
- Annotation entities update **reactively** for every connected editor.

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
