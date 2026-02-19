---
Name: Collaborative Editing
Category: Documentation
Description: How to use collaborative markdown editing with comments and track changes
Icon: /static/storage/content/MeshWeaver/Documentation/DataMesh/CollaborativeEditing/icon.svg
---

Work together on documents in real-time with comments and suggestions.

## Adding Comments

To add a comment, select the text you want to comment on and click the **Comment** button in the toolbar. Your comment will appear as a popover when you click on the highlighted text.

### Example: A paragraph with comments

The following paragraph demonstrates how comments appear in a document:

> MeshWeaver is a <!--comment:c1-->powerful platform<!--/comment:c1--> for building <!--comment:c2-->collaborative applications<!--/comment:c2-->. It provides real-time synchronization and <!--comment:c3-->conflict-free editing<!--/comment:c3-->.

In this example:
- "powerful platform" has a comment asking for more specific metrics
- "collaborative applications" has a suggestion to add examples
- "conflict-free editing" has a question about the technology used

---

## Making Suggestions (Track Changes)

When you want to suggest changes without directly editing, use **Track Changes** mode. Your edits will appear as suggestions that others can accept or reject.

### Suggested Additions

Text that you add appears with a <!--insert:i1:Alice:Dec 18-->green underline<!--/insert:i1-->. Others can review and accept or reject your addition.

> The quarterly report shows <!--insert:i2:Bob:Dec 19-->significant growth of 25%<!--/insert:i2--> in user engagement.

### Suggested Deletions

Text you want to remove appears with a <!--delete:d1:Carol:Dec 20-->red strikethrough<!--/delete:d1-->. The original text remains visible until the change is accepted.

> Please review the <!--delete:d2:Alice:Dec 21-->outdated and no longer relevant<!--/delete:d2--> documentation before the meeting.

### Combined Example

Here's a paragraph with multiple suggestions:

> Our team has completed the <!--delete:d3:Bob:Dec 22-->initial<!--/delete:d3--><!--insert:i3:Bob:Dec 22-->comprehensive<!--/insert:i3--> analysis of the <!--comment:c4-->market trends<!--/comment:c4-->. We recommend <!--insert:i4:Alice:Dec 23-->immediate action on the following priorities<!--/insert:i4-->:
>
> 1. <!--insert:i5:Bob:Dec 23-->Expand into European markets<!--/insert:i5-->
> 2. <!--delete:d4:Carol:Dec 23-->Reduce marketing budget<!--/delete:d4--><!--insert:i6:Carol:Dec 23-->Reallocate marketing spend to digital channels<!--/insert:i6-->
> 3. Improve customer <!--delete:d5:Alice:Dec 24-->satisfaction<!--/delete:d5--><!--insert:i7:Alice:Dec 24-->retention rates<!--/insert:i7-->

---

## Reviewing Changes

### Accepting Changes

Click the **checkmark** next to a suggestion to accept it:
- **Accept insertion**: The suggested text becomes permanent
- **Accept deletion**: The marked text is removed

### Rejecting Changes

Click the **X** next to a suggestion to reject it:
- **Reject insertion**: The suggested text is removed
- **Reject deletion**: The original text is kept

### Accept All / Reject All

Use the toolbar buttons to accept or reject all pending changes at once.

---

## Working with Multiple Collaborators

When multiple people edit the same document:

- You'll see colored cursors showing where others are working
- Each person's suggestions are color-coded
- Comments show the author's name and timestamp
- Changes sync automatically - no need to save

### Example: Team Review Session

> **Project Proposal** *(3 collaborators editing)*
>
> The <!--comment:c5-->proposed timeline<!--/comment:c5--> for Phase 1 is <!--delete:d6:Bob:Dec 26-->6 months<!--/delete:d6--><!--insert:i8:Bob:Dec 26-->4 months<!--/insert:i8-->. This <!--insert:i9:Alice:Dec 26-->aggressive but achievable<!--/insert:i9--> schedule requires:
>
> - <!--comment:c6-->Additional resources<!--/comment:c6--> from the engineering team
> - <!--delete:d7:Carol:Dec 27-->Weekly<!--/delete:d7--><!--insert:i10:Carol:Dec 27-->Daily<!--/insert:i10--> standup meetings
> - <!--insert:i11:Alice:Dec 27-->A dedicated project manager<!--/insert:i11-->

---

## Tips for Effective Collaboration

1. **Use comments for questions** - Don't change text when you're unsure; ask first
2. **Make atomic suggestions** - One change per suggestion is easier to review
3. **Resolve conversations** - Mark comment threads as resolved when done
4. **Review before accepting** - Read through all suggestions before bulk-accepting
5. **Add context** - Include a note explaining why you're suggesting a change
