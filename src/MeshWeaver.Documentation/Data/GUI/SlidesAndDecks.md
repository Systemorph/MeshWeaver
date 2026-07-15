---
Name: Slides & Decks
Category: Documentation
Description: The Slide and Deck node types — a Slide is one pure-content page, a Deck declares slide order EXTERNALLY in its manifest and auto-renders a hidable side-nav + Present.
Icon: /static/NodeTypeIcons/presentation.svg
---

A presentation on the mesh is two node types working together. A **Slide** is one page — pure content, carrying no order of its own. A **Deck** is the ordered set of slides, and the deck owns the order: it lists its slides, in sequence, in a manifest on the deck node itself. Both ship from the platform (`AddGraph()`), so you never write a layout area for them — the Slide and Deck views already render the stage, the presenter bar, the collapsible side-nav, and Present.

The point of this design: **the order lives in ONE place, external to the slides.** Reorder a deck, insert a page, or drop one by editing a single list — never by sweeping an `Order` field across every slide, and two slides can never fight over the same position.

<svg viewBox="0 0 760 250" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <rect x="20" y="20" width="300" height="210" rx="10" fill="#1e2a3a" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5"/>
  <text x="170" y="44" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="11" font-weight="600" letter-spacing="1">DECK NODE</text>
  <rect x="40" y="56" width="260" height="60" rx="8" fill="#4f6bed"/>
  <text x="170" y="78" text-anchor="middle" fill="#fff" font-weight="600" font-size="12">DeckContent.Slides</text>
  <text x="170" y="98" text-anchor="middle" fill="#fff" font-size="11" fill-opacity=".9">[ "intro", "anatomy", "recap" ]  ← the order</text>
  <text x="170" y="140" text-anchor="middle" fill="currentColor" fill-opacity=".6" font-size="11">one manifest = the whole sequence</text>
  <text x="170" y="160" text-anchor="middle" fill="currentColor" fill-opacity=".6" font-size="11">edit here to reorder</text>
  <rect x="360" y="20" width="380" height="210" rx="10" fill="#1a2530" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5"/>
  <text x="550" y="44" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="11" font-weight="600" letter-spacing="1">CHILD SLIDES — PURE CONTENT</text>
  <rect x="380" y="56" width="340" height="34" rx="6" fill="#26a69a"/>
  <text x="550" y="77" text-anchor="middle" fill="#fff" font-size="12">intro — one big picture + notes</text>
  <rect x="380" y="98" width="340" height="34" rx="6" fill="#26a69a"/>
  <text x="550" y="119" text-anchor="middle" fill="#fff" font-size="12">anatomy — one big picture + notes</text>
  <rect x="380" y="140" width="340" height="34" rx="6" fill="#26a69a"/>
  <text x="550" y="161" text-anchor="middle" fill="#fff" font-size="12">recap — one big picture + notes</text>
  <text x="550" y="200" text-anchor="middle" fill="currentColor" fill-opacity=".6" font-size="11">no Order field — the slide is just content</text>
  <line x1="320" y1="86" x2="378" y2="73" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5"/>
  <line x1="320" y1="90" x2="378" y2="115" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5"/>
  <line x1="320" y1="94" x2="378" y2="157" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5"/>
</svg>

# The Slide node

A Slide's `Content` is a `SlideContent` record:

- **`Content`** — the slide body as **markdown**. Raw HTML and SVG pass through the markdown pipeline unchanged, so a slide can be a bullet list or a full-bleed illustration.
- **`Notes`** — speaker notes (markdown), shown only in the Notes view. This is where the *teaching* lives.
- **`Background`** — optional CSS background for the stage (e.g. `linear-gradient(135deg, #667eea 0%, #764ba2 100%)`). Null → the theme-aware default gradient.

**Classroom style.** A good slide is a hook, not a handout: little text on the stage, ideally **one big inline-SVG picture** that fills the 16:9 stage, and the actual explanation in `Notes`. Size type with `clamp()` (`font-size: clamp(18px, 3vw, 42px)`) so it scales with the stage instead of looking right at only one width.

A Slide has **three views**:

- **Content** (default) — the slide on a 16:9 stage with a slim presenter bar underneath (◀ Prev · "Slide n / N" · Deck · Present · Next ▶). Clicking the stage advances to the next slide.
- **Present** — the chrome-free stage: click to advance, a small corner counter the only overlay. Open it full-screen to present.
- **Notes** — the speaker notes plus a compact preview of the slide.

# The Deck node — order is external

A **Deck** (`NodeType = "Deck"`) is a node whose `Content` is a `DeckContent` record:

- **`Title`** — optional display title for the welcome stage (falls back to the node name).
- **`Description`** — optional markdown intro shown on the deck's welcome stage.
- **`Slides`** — an explicit **ordered list of references**. **This IS the deck's order**, kept exactly as declared (never re-sorted). Each entry is a child id (relative — `"intro"`) or a full path to a slide **anywhere** in the mesh, so the same slide can appear in many decks in different orders.
- **`Query`** — an optional GitHub-style node query that selects the deck's slides **dynamically**, as a live (synced) set — used only when `Slides` is empty.

**How a deck picks its slides — precedence:**

1. **`Slides` manifest** (non-empty) → exactly those references, **in the order declared**. Use this for a curated deck, or one that draws from a shared pool that lives elsewhere.
2. **`Query`** (when there is no manifest) → the live query result, ordered by each slide's **`MeshNode.Order`** (nulls last, ties by path).
3. **Neither** → the deck defaults to its **own subtree** (`path:{deck} scope:descendants`), ordered by `MeshNode.Order`. So a deck whose slides are simply its children needs **no manifest at all** — reorder by editing each slide's `Order`.

(Query/subtree results skip the deck node itself and any `_`-prefixed governance node.)

The Deck's views are automatic:

- **Overview** (default) — a splitter with a **hidable (collapsible) left side-nav** listing the slides in manifest order (each labeled by its slide's name), and a right welcome stage carrying the intro and a **▶ Present** button that opens the first slide chrome-free.
- **Present** — redirects straight into the first manifest slide's Present view, starting the walk.

**How the order flows to the slides.** When a Slide's parent is a Deck with a non-empty `Slides` manifest, the Slide views resolve prev / next / index / count from that manifest — the deck's declared order wins over any `MeshNode.Order` on the slides. When the parent is **not** a Deck (a Markdown or Space parent, as in older decks), the slides fall back to ordering by `MeshNode.Order`. So existing decks keep working unchanged, and new decks get external order for free.

# Present & click-to-advance

Presenting is standard navigation — no bespoke messaging. The stage is a click-to-advance surface: a click renders a `RedirectControl` to the next slide (staying in Present mode when presenting), the same href/redirect mechanism every other area uses. Prev/Next buttons in the presenter bar navigate the same way.

# Course usage — a Deck can sequence module pages too

The manifest is not limited to slides. A Deck can order **any** child pages — the Markdown module pages of a course, for instance — giving the course a side-nav and a linear sequence from a single node, without touching each page. List the module-page ids in the deck's `Slides`, in teaching order, and the deck's side-nav becomes the course TOC + walk-through. (Slide-to-slide click-to-advance is a Slide-view feature; module pages get the side-nav and ordered sequence.)

# Worked shape

Create the deck, then the slides as its children, then list the order — once — in the deck:

```csharp
// The deck — the manifest is the order (external to the slides).
new MeshNode("widgets", "MyCourse")
{
    NodeType = "Deck",
    Name = "Intro to Widgets",
    Content = new DeckContent
    {
        Title = "Intro to Widgets",
        Description = "A five-minute tour. Press **Present** to begin.",
        Slides = ["intro", "anatomy", "recap"]   // ← THIS is the order
    }
};

// The slides — pure content, no order of their own, created in any sequence.
new MeshNode("anatomy", "MyCourse/widgets") { NodeType = "Slide", Name = "Anatomy", Content = new SlideContent { /* one big SVG + notes */ } };
new MeshNode("intro",   "MyCourse/widgets") { NodeType = "Slide", Name = "What is a widget?", Content = new SlideContent { /* ... */ } };
new MeshNode("recap",   "MyCourse/widgets") { NodeType = "Slide", Name = "Recap", Content = new SlideContent { /* ... */ } };
```

Reorder later by editing only the deck — the slides never change:

```csharp
workspace.GetMeshNodeStream("MyCourse/widgets")
    .Update(node => node with { Content = ((DeckContent)node.Content) with { Slides = ["intro", "recap", "anatomy"] } })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "reorder failed"));
```

# See Also

- [Layout Areas](../LayoutAreas) — what an area is and how the Slide/Deck views are registered
- [Combining Layout Areas in Markdown](../CombiningLayoutAreas) — embed a deck or slide area inline with prose
- [Configurable Pages](../ConfigurablePages) — the reusable `@@("area/…")` home-page areas
- [Data Binding](../DataBinding) — how a view stays live as its node changes
