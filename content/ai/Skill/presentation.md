---
nodeType: Skill
name: /presentation
description: Produce a slide presentation — author Slide pages (one idea, one big visual, prose in speaker Notes), order them EXTERNALLY in a Deck manifest, and present with the automatic side-nav + Present mode.
icon: Presentation
category: Skills
order: 17
---

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
