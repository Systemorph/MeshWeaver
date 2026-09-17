---
Name: Unreadable Content Is Not Empty Content
Category: GUI
Description: >-
  An empty state is an INVITATION, and rendering it over content the view could not interpret is how
  the stored text gets overwritten. The three states a content read has, which one may be collapsed
  into which, and the one place a view may not guess.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/><path d="M12 12v4"/><path d="M12 19h.01"/></svg>
---

# Unreadable Content Is Not Empty Content

**A view that renders an empty state is making a claim about the data, and it has THREE possible
answers, not two: there is none, there is some, and there is some that I cannot interpret.** Folding
the third into the first is not a cosmetic shortcut — it is how authored text gets destroyed.

## Why the collapse costs data

The markdown page's empty state reads *"No content yet. Use the menu to start editing."* That is an
**invitation**, and a reader acts on it: they start typing, and the first save replaces whatever was
stored with what they typed. Where the node really was empty that is exactly right. Where its
content was merely unreadable, the document was still in the store until that save.

Measured on a customer workspace, 2026-09-17
([#4600](https://github.com/Systemorph/MeshWeaver/issues/4600)): a `Markdown` node created with

```json
{"markdown": "# … Company Profile\n…"}
```

— a member `MarkdownContent` does not declare — rendered as an empty node from v1. Nothing errored,
nothing warned, and the page was indistinguishable from a genuinely blank one.

The mechanism was one return statement. `GetMarkdownContent` answered `string.Empty` from its final
fall-through for every shape it does not recognise, so *"this node has no content"* and *"this node
has content I could not read"* arrived at the caller **as the same value**.

## The three states

`MarkdownOverviewLayoutArea.ReadMarkdownContent` returns them explicitly:

| State | What the view does |
|---|---|
| `Absent` | The authoring placeholder — the invitation, which this case is for |
| `Present` | The markdown body |
| `Unreadable` | A diagnostic naming what IS stored, and no invitation. Logged at Warning |

## 🚨 What may be called Unreadable, and what may not

Only a raw `JsonElement` — the shape content has when **nothing in the process could type it**.
Typed CLR content of some other shape reads as `Absent` exactly as before: `GetMarkdownContent` is
called from the version diff and the notebook view for nodes that are not markdown at all, and
calling a node's own perfectly readable typed content "unreadable" would be false.

Two further shapes stay `Absent` on purpose, because they ARE empty: `{}`, and an object carrying
nothing but a `$type`.

## The string accessor stays, and stays honest about its limits

`GetMarkdownContent` still returns a plain `string` and is unchanged for its four callers — they ask
*"is there prose to show or diff"*, and for that question an empty string is a complete answer. Its
doc comment now states the limit it cannot express, so the next caller does not repeat the mistake:
**a view that tells the USER the node is empty must read `ReadMarkdownContent`.**

## The sibling that was checked and left alone

`ContentLayoutArea` (MeshWeaver.ContentCollections) has a private `GetMarkdownContent` with the same
collapse. It is deliberately not changed here: its placeholder is *"No content available."*, which
states a fact rather than inviting an edit, so the destructive path this page is about does not
exist there. If that copy ever becomes an invitation, it needs this treatment first.

## Related

- The write-side half is [#4601](https://github.com/Systemorph/MeshWeaver/issues/4601): where such a
  payload comes from, and why the write boundary now refuses it.
- [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) — `ContentAs<T>` over a cast,
  and what a silent null looks like from outside.
