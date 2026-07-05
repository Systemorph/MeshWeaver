---
nodeType: Skill
name: /slide
description: Author classroom slides and decks — a Slide is one pure-content page (one big picture, teaching prose in notes); a Deck orders slides EXTERNALLY in its manifest and auto-renders a hidable side-nav + Present.
icon: Presentation
category: Skills
order: 16
---

A **Slide** is one page of a presentation. A **Deck** is the ordered set of slides — and the deck owns the order, declared EXTERNALLY in the deck node, not on each slide. Both ship from the platform (`AddGraph()`), so you author them as data: create nodes, set content, list the order. You never write a layout area for a deck — the Slide and Deck views already render the stage, the presenter bar, the hidable side-nav, and Present.

Before authoring, skim [Slides & Decks](/Doc/GUI/SlidesAndDecks).

# 1. The Slide node — pure content, classroom style

A Slide's `Content` is a `SlideContent` record:

- **`Content`** — the slide body as **markdown**. Raw HTML and SVG pass through the markdown pipeline unchanged, so a slide can be anything from a bullet list to a full-bleed illustration.
- **`Notes`** — speaker notes (markdown), shown only in the Notes view. This is where the *teaching* lives.
- **`Background`** — optional CSS background for the stage (e.g. `linear-gradient(135deg, #667eea 0%, #764ba2 100%)`). Null → the theme-aware default gradient.

**Classroom style — the rule that makes a deck teach, not overwhelm:**

- **Little text on the stage.** One idea per slide. A title and at most a few short lines.
- **ONE big picture per slide.** Prefer a single inline **SVG** illustration that fills the stage — a diagram, a metaphor, a shape — over a wall of bullets. The stage is 16:9; let the picture own it.
- **Put the prose in `Notes`.** The explanation, the worked example, the "why" — that is speaker-notes material, not stage material. The stage is the hook; the notes are the lesson.
- **Responsive type.** Size text with `clamp()` so it scales with the stage: `font-size: clamp(18px, 3vw, 42px)`. Never hard-code a pixel size that only looks right at one width.

A slide carries **no order of its own** — ordering is the deck's job (below). The slide is just content.

```csharp
new MeshNode("intro", "MyCourse/widgets")
{
    NodeType = "Slide",
    Name = "What is a widget?",
    Content = new SlideContent
    {
        Content = """
            # What is a widget?

            <svg viewBox="0 0 400 220" style="width:100%;height:auto">
              <circle cx="200" cy="110" r="80" fill="#4f6bed"/>
              <text x="200" y="118" text-anchor="middle" fill="white"
                    style="font: 600 clamp(18px,3vw,42px) sans-serif">widget</text>
            </svg>
            """,
        Notes = "A widget is the smallest unit of work. Draw the analogy to a Lego "
              + "brick: self-contained, composable, and useless in isolation. Ask the "
              + "class for three things in their world that behave like widgets."
    }
}
```

# 2. The Deck node — order is EXTERNAL

A **Deck** (`NodeType = "Deck"`) is a node whose `Content` is a `DeckContent` record:

- **`Title`** — optional display title for the welcome stage (falls back to the node name).
- **`Description`** — optional markdown intro shown on the deck's welcome stage.
- **`Slides`** — the **ordered list** of child references. **This IS the deck's order.** Each entry is a child id (relative — `"intro"`) or a full path. Reorder the deck by editing this one list; the slides never change.

Why external order matters: the sequence lives in ONE place. Inserting a slide, dropping one, or re-sequencing the whole deck is a single edit to `Slides` — you never sweep an `Order` field across every slide, and two slides can't fight over the same position.

The Deck's views are automatic:

- **Overview** (default) → a splitter with a **hidable (collapsible) left side-nav** listing the slides in manifest order (each labeled by its slide's name), and a right welcome stage with the intro and a **▶ Present** button that opens the first slide chrome-free.
- **Present** → redirects straight into the first slide's Present view, starting the walk.

# 3. Present & click-to-advance

Presenting is standard navigation, no bespoke messaging:

- The **stage** is click-to-advance: clicking a slide navigates to the next slide (in the same mode — staying in Present when presenting). It uses a `RedirectControl` / href, the framework's normal navigation.
- **Content** view = stage + a slim presenter bar (◀ Prev · "Slide n / N" · Deck · Present · Next ▶).
- **Present** view = the chrome-free stage with only a corner counter — open it full-screen to present.
- **Prev/Next/index/count** come from the deck's manifest when the slide's parent is a Deck; otherwise they fall back to sibling `MeshNode.Order`. Either way the slide stays pure content.

# 4. Course usage — a Deck can sequence module pages too

The manifest isn't limited to slides. A Deck can order **any** child pages — Markdown module pages of a course, for instance — giving the course a side-nav and a linear sequence from a single node, without touching each page. List the module page ids in the deck's `Slides`, in teaching order, and the deck's side-nav becomes the course TOC + walk-through. (Prev/Next click-to-advance is a Slide-view feature; module pages get the side-nav + ordered sequence.)

# 5. Worked shape — a deck + its slides

Create the deck, then the slides as its children, then list the order in the deck. Order is set ONCE, in the deck:

```csharp
// 1. The deck — the manifest is the order (external to the slides).
new MeshNode("widgets", "MyCourse")
{
    NodeType = "Deck",
    Name = "Intro to Widgets",
    Content = new DeckContent
    {
        Title = "Intro to Widgets",
        Description = "A five-minute tour. Press **Present** to begin.",
        Slides = ["intro", "anatomy", "composing", "recap"]   // ← THIS is the order
    }
}

// 2. The slides — pure content, no order of their own, created in any sequence.
new MeshNode("anatomy", "MyCourse/widgets") { NodeType = "Slide", Name = "Anatomy", Content = new SlideContent { /* one big SVG + notes */ } };
new MeshNode("intro",     "MyCourse/widgets") { NodeType = "Slide", Name = "What is a widget?", Content = new SlideContent { /* ... */ } };
// … "composing", "recap" likewise.
```

Reorder later by editing only the deck:

```csharp
workspace.GetMeshNodeStream("MyCourse/widgets")
    .Update(node => node with { Content = ((DeckContent)node.Content) with { Slides = ["intro", "composing", "anatomy", "recap"] } })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "reorder failed"));
```

# The litmus test

If you're putting a `Order` number on each slide to sequence a deck, or pasting the lesson text onto the stage instead of into `Notes`, or hand-building a nav for the slides — **stop**. The order goes in the deck's `Slides` manifest (one place), the teaching goes in `Notes`, and the side-nav + Present are automatic. Full reference: [Slides & Decks](/Doc/GUI/SlidesAndDecks).
