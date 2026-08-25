---
nodeType: Skill
name: /presentation
description: Produce a slide presentation — author Slide pages (one idea, one big visual, prose in speaker Notes), order them EXTERNALLY in a Deck manifest, and present with the automatic side-nav + Present mode.
icon: Presentation
category: Skills
order: 17
---

# 🚨 WRITE INTO THE USER'S OWN HOME — never into the space you are reading

The chat you are running in is almost always opened **from a page**, so your context is that page's
node — a course lesson, a doc, someone else's space. On any of those the signed-in reader typically
holds **Read only**: a course is gated (entitlement = the `Viewer` role), a plugin/doc space is
GitSynced and writable by `system-security` alone.

Create your output there and the write fails with

> ⚠️ Access denied: Create permission required for node '{Space}/…'

which is what the person sees **instead of the thing they asked for**. It is not a permissions bug to
report, not something for them to "get access" to, and not a reason to stop: it is the wrong target.

**Everything you CREATE belongs under the user's own home — `{userId}/…`** (e.g.
`felice.buergi/DiceGame`, `felice.buergi/Skill/…`). That covers the program, the deck, the document,
the poster, the data node, the scratch node you needed on the way — and the **thread / activity**
that produces them.

- **Resolve the user id from the signed-in identity**, never from the page you happen to be on.
  Being *on* `AgenticPrimerDe/02-CodeWunsch` says what the request is ABOUT, not where it goes.
- **Read from anywhere; write only there.** Reading the lesson to understand the ask is right;
  writing next to it is not.
- If the user explicitly names a target they own, use it. Otherwise **default to their home** — do
  not ask them where to put it; they mostly do not know the mesh has places.
- If a write is denied anyway, **do not retry against the same space and do not escalate to the
  user**: re-target their home, finish the job, and tell them one line about where it landed.
- Say where it went when you are done ("I put it in your space at `…`"), with a link — a thing
  created somewhere the user cannot find is a thing you did not create.

Build a **presentation** by authoring it as data — you never write a layout area or a nav. A presentation is a **Deck** (`NodeType = "Deck"`) whose ordered manifest points at **Slide** pages (`NodeType = "Slide"`). Both ship from the platform (`AddGraph()`), and their views already render the stage, the presenter bar, a hidable side-nav, and Present. This skill is the end-to-end workflow; for the full authoring reference see the **[/slide](@/Skill/slide)** skill and [Slides & Decks](/Doc/GUI/SlidesAndDecks).

# The workflow

1. **Outline first.** Decide the sequence of ideas — one idea per slide. Write the outline before any node; the outline becomes the deck's manifest order.
2. **Author each Slide** (pure content, no order of its own).
3. **Create the Deck** and list the slide ids in teaching order in its `Slides` manifest — the order lives in ONE place.
4. **Present** — open the deck and press ▶ Present; the stage is click-to-advance.

# 1. Slide — one idea, one big visual, prose in the notes

A Slide's `Content` is a `SlideContent` record:

- **`Content`** — the stage, as **markdown** (raw HTML/SVG passes through). Keep it sparse: a title and a few short lines, or ONE full-bleed inline **SVG** that owns the 16:9 stage. Size text with `clamp()` (e.g. `font-size: clamp(18px,3vw,42px)`) so it scales with the stage — never a fixed px.
- **`Notes`** — speaker notes (markdown), shown only in the Notes view. **This is where the talk lives** — the explanation, the worked example, the "why." The stage is the hook; the notes are the script.
- **`Background`** — optional CSS background (e.g. `linear-gradient(135deg,#667eea,#764ba2)`); null → the theme default.

**The rule that makes a presentation land: little text on the stage, the lesson in `Notes`, one picture per slide.** If you're pasting paragraphs onto the stage, move them to `Notes`.

# 2. Deck — the order is EXTERNAL

A Deck's `Content` is a `DeckContent` record:

- **`Title`** — welcome-stage title (falls back to the node name).
- **`Description`** — markdown intro shown on the welcome stage.
- **`Slides`** — the **ordered list** of child references (relative ids like `"intro"`, or full paths). **This list IS the presentation's order.** Reorder / insert / drop by editing only this list; the slides never change and can't fight over a position.

The Deck's views are automatic: **Overview** = a collapsible left side-nav (slides in manifest order) + a right welcome stage with a **▶ Present** button; **Present** = jumps straight into the first slide, chrome-free.

# 3. Presenting

Standard navigation, no bespoke messaging: the **stage is click-to-advance** (click → next slide, staying in Present). The **Content** view adds a slim presenter bar (◀ Prev · "Slide n / N" · Deck · Present · Next ▶); the **Present** view is the chrome-free stage with a corner counter — open it full-screen to present. Prev/Next/index/count come from the deck's manifest.

# Worked shape

```csharp
// The deck — the manifest is the order (external to the slides).
new MeshNode("pitch", "MySpace")
{
    NodeType = "Deck",
    Name = "Product Pitch",
    Content = new DeckContent
    {
        Title = "Product Pitch",
        Description = "A five-minute story. Press **Present** to begin.",
        Slides = ["hook", "problem", "solution", "ask"]   // ← THIS is the order
    }
};

// The slides — pure content, created in any sequence, no Order field.
new MeshNode("hook", "MySpace/pitch") { NodeType = "Slide", Name = "The hook",
    Content = new SlideContent { Content = "# ...one big SVG...", Notes = "Open with the customer's pain. Ask a show-of-hands question." } };
// … "problem", "solution", "ask" likewise.
```

Reorder later by editing only the deck's `Slides` — never an `Order` on each slide.

# Litmus test

Putting an `Order` on each slide, pasting the script onto the stage instead of into `Notes`, or hand-building a nav → **stop**. Order goes in the deck's `Slides` manifest, the talk goes in `Notes`, the side-nav + Present are automatic. Deeper reference: **[/slide](@/Skill/slide)** · [Slides & Decks](/Doc/GUI/SlidesAndDecks).
