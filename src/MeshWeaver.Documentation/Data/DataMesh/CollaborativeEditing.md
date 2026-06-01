---
Name: Collaborative Editing
Category: Documentation
Description: Real-time collaborative markdown editing with comments, track changes, and annotation satellite entities
Icon: /static/DocContent/DataMesh/CollaborativeEditing/icon.svg
---

Work together on documents in real time — comment on passages, propose edits as tracked suggestions, and accept or reject changes without ever leaving the document.

---

## How It Works: Annotations as Satellite Entities

MeshWeaver stores annotations **beside** the document, not inside it. Every comment or tracked change is a satellite entity living in a dedicated partition next to the document node:

| Annotation type | Partition | Extension class |
|---|---|---|
| Comment | `_Comment` | `CommentsExtensions` |
| Tracked change | `_Tracking` | `AnnotationExtensions` |

The markdown source itself holds lightweight **inline markers** (`<!--comment:id-->text<!--/comment:id-->`) as the authoritative record of which text range an annotation covers. Positions are always *derived* from those markers at load time — they are never stored on the entities.

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

> MeshWeaver is a <!--comment:c1-->powerful platform<!--/comment:c1--> for building <!--comment:c2-->collaborative applications<!--/comment:c2-->. It provides real-time synchronization and <!--comment:c3-->conflict-free editing<!--/comment:c3-->.

In the above example three comment markers are embedded in the source:

- `c1` — "powerful platform" flagged for more specific metrics
- `c2` — "collaborative applications" tagged with a request for examples
- `c3` — "conflict-free editing" questioned about the underlying technology

---

## Making Suggestions (Track Changes)

**Suggest Edit** lets you propose changes without altering the document directly. A `TrackedChange` entity is created; reviewers can accept or reject each suggestion individually — or all at once.

### Suggested additions

Proposed new text gets a <!--insert:i1:Alice:Dec 18-->green underline<!--/insert:i1-->.

> The quarterly report shows <!--insert:i2:Bob:Dec 19-->significant growth of 25%<!--/insert:i2--> in user engagement.

### Suggested deletions

Text proposed for removal gets a <!--delete:d1:Carol:Dec 20-->red strikethrough<!--/delete:d1-->. The original text stays visible until the suggestion is decided.

> Please review the <!--delete:d2:Alice:Dec 21-->outdated and no longer relevant<!--/delete:d2--> documentation before the meeting.

### Combined example

> Our team has completed the <!--delete:d3:Bob:Dec 22-->initial<!--/delete:d3--><!--insert:i3:Bob:Dec 22-->comprehensive<!--/insert:i3--> analysis of the <!--comment:c4-->market trends<!--/comment:c4-->. We recommend <!--insert:i4:Alice:Dec 23-->immediate action on the following priorities<!--/insert:i4-->:
>
> 1. <!--insert:i5:Bob:Dec 23-->Expand into European markets<!--/insert:i5-->
> 2. <!--delete:d4:Carol:Dec 23-->Reduce marketing budget<!--/delete:d4--><!--insert:i6:Carol:Dec 23-->Reallocate marketing spend to digital channels<!--/insert:i6-->
> 3. Improve customer <!--delete:d5:Alice:Dec 24-->satisfaction<!--/delete:d5--><!--insert:i7:Alice:Dec 24-->retention rates<!--/insert:i7-->

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
> The <!--comment:c5-->proposed timeline<!--/comment:c5--> for Phase 1 is <!--delete:d6:Bob:Dec 26-->6 months<!--/delete:d6--><!--insert:i8:Bob:Dec 26-->4 months<!--/insert:i8-->. This <!--insert:i9:Alice:Dec 26-->aggressive but achievable<!--/insert:i9--> schedule requires:
>
> - <!--comment:c6-->Additional resources<!--/comment:c6--> from the engineering team
> - <!--delete:d7:Carol:Dec 27-->Weekly<!--/delete:d7--><!--insert:i10:Carol:Dec 27-->Daily<!--/insert:i10--> standup meetings
> - <!--insert:i11:Alice:Dec 27-->A dedicated project manager<!--/insert:i11-->

---

## Tips for Effective Collaboration

1. **Comment before you change** — if you are uncertain, ask rather than edit.
2. **Keep suggestions atomic** — one logical change per suggestion makes review faster.
3. **Resolve threads when done** — mark comment threads resolved to keep the sidebar clean.
4. **Read before bulk-accepting** — scan all pending suggestions before using Accept All.
5. **Add context** — a brief note explaining *why* you propose a change helps reviewers decide quickly.
