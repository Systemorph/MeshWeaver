---
Name: Side Panel & Main Panel
Category: Documentation
Description: How the side panel and main panel work together for multitasking, AI chat, and contextual navigation without losing your place
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="2"/><line x1="15" y1="3" x2="15" y2="21"/></svg>
---

The portal uses a **split-view layout**: a resizable **side panel** that slides in from the right, and a **main panel** that always shows the current page. The side panel is designed for AI chat threads, but it can host any content — and it remembers exactly where you left off.
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 340" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".6"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="340" rx="10" fill="none" stroke="currentColor" stroke-opacity=".15" stroke-width="1"/>
  <rect x="16" y="16" width="440" height="308" rx="8" fill="#1e3a5f" stroke="#1e88e5" stroke-width="1.5"/>
  <rect x="16" y="16" width="440" height="36" rx="8" fill="#1e88e5"/>
  <rect x="16" y="44" width="440" height="8" rx="0" fill="#1e88e5"/>
  <text x="236" y="39" text-anchor="middle" fill="#fff" font-weight="bold" font-size="14">Main Panel</text>
  <text x="236" y="74" text-anchor="middle" fill="#fff" fill-opacity=".55" font-size="12">Current URL / page content</text>
  <rect x="40" y="90" width="392" height="100" rx="6" fill="#0d2240" stroke="currentColor" stroke-opacity=".2" stroke-width="1"/>
  <text x="236" y="112" text-anchor="middle" fill="#fff" fill-opacity=".7" font-size="12">Thread (full-page view)</text>
  <text x="236" y="132" text-anchor="middle" fill="#fff" fill-opacity=".45" font-size="11">← back link · title · message history</text>
  <rect x="56" y="152" width="360" height="24" rx="5" fill="#1e3a5f" stroke="#1e88e5" stroke-opacity=".5" stroke-width="1"/>
  <text x="236" y="169" text-anchor="middle" fill="#fff" fill-opacity=".6" font-size="11">Input area (message box)</text>
  <rect x="40" y="206" width="392" height="52" rx="6" fill="#0d2240" stroke="currentColor" stroke-opacity=".2" stroke-width="1"/>
  <text x="236" y="230" text-anchor="middle" fill="#fff" fill-opacity=".55" font-size="12">Navigate independently</text>
  <text x="236" y="248" text-anchor="middle" fill="#fff" fill-opacity=".35" font-size="11">side panel content is preserved</text>
  <rect x="40" y="272" width="180" height="36" rx="6" fill="#0d2240" stroke="currentColor" stroke-opacity=".2" stroke-width="1"/>
  <text x="130" y="295" text-anchor="middle" fill="#fff" fill-opacity=".45" font-size="11">Page reload</text>
  <rect x="236" y="272" width="196" height="36" rx="6" fill="#0d2240" stroke="currentColor" stroke-opacity=".2" stroke-width="1"/>
  <text x="334" y="295" text-anchor="middle" fill="#fff" fill-opacity=".45" font-size="11">State from localStorage</text>
  <rect x="468" y="16" width="12" height="308" rx="4" fill="#e53935" stroke="none" opacity=".7"/>
  <text x="474" y="178" text-anchor="middle" fill="#fff" font-size="11" writing-mode="tb">drag to resize</text>
  <rect x="492" y="16" width="252" height="308" rx="8" fill="#1a3a2a" stroke="#43a047" stroke-width="1.5"/>
  <rect x="492" y="16" width="252" height="36" rx="8" fill="#43a047"/>
  <rect x="492" y="44" width="252" height="8" rx="0" fill="#43a047"/>
  <text x="618" y="39" text-anchor="middle" fill="#fff" font-weight="bold" font-size="14">Side Panel</text>
  <rect x="504" y="60" width="228" height="28" rx="5" fill="#0d2a18" stroke="currentColor" stroke-opacity=".2" stroke-width="1"/>
  <text x="528" y="79" fill="#fff" fill-opacity=".75" font-size="11">+ New</text>
  <text x="578" y="79" fill="#fff" fill-opacity=".75" font-size="11">↺ Resume</text>
  <text x="700" y="79" text-anchor="middle" fill="#fff" fill-opacity=".75" font-size="11">↗  ✕</text>
  <rect x="504" y="98" width="228" height="140" rx="6" fill="#0d2a18" stroke="currentColor" stroke-opacity=".2" stroke-width="1"/>
  <text x="618" y="120" text-anchor="middle" fill="#fff" fill-opacity=".7" font-size="12">AI Chat Thread</text>
  <rect x="516" y="132" width="204" height="18" rx="4" fill="#1a3a2a" stroke="currentColor" stroke-opacity=".15"/>
  <text x="618" y="145" text-anchor="middle" fill="#fff" fill-opacity=".45" font-size="10">user message</text>
  <rect x="516" y="156" width="204" height="18" rx="4" fill="#1a3a2a" stroke="#43a047" stroke-opacity=".4"/>
  <text x="618" y="169" text-anchor="middle" fill="#43a047" fill-opacity=".85" font-size="10">assistant response</text>
  <rect x="516" y="180" width="204" height="18" rx="4" fill="#1a3a2a" stroke="currentColor" stroke-opacity=".15"/>
  <text x="618" y="193" text-anchor="middle" fill="#fff" fill-opacity=".45" font-size="10">user message</text>
  <rect x="516" y="204" width="204" height="20" rx="4" fill="#0d2a18" stroke="#43a047" stroke-opacity=".5" stroke-width="1"/>
  <text x="618" y="218" text-anchor="middle" fill="#fff" fill-opacity=".55" font-size="10">@ type a message…</text>
  <rect x="504" y="250" width="110" height="28" rx="5" fill="#1a3a2a" stroke="#43a047" stroke-opacity=".5"/>
  <text x="559" y="269" text-anchor="middle" fill="#43a047" font-size="11">Agent selector</text>
  <rect x="622" y="250" width="110" height="28" rx="5" fill="#1a3a2a" stroke="#43a047" stroke-opacity=".5"/>
  <text x="677" y="269" text-anchor="middle" fill="#43a047" font-size="11">Model selector</text>
  <rect x="504" y="290" width="228" height="28" rx="5" fill="#0d2a18" stroke="currentColor" stroke-opacity=".2" stroke-width="1"/>
  <text x="618" y="309" text-anchor="middle" fill="#fff" fill-opacity=".5" font-size="11">Persisted in localStorage</text>
  <line x1="700" y1="68" x2="700" y2="52" stroke="#f57c00" stroke-width="1.5" stroke-opacity=".8" marker-end="url(#arr)"/>
  <path d="M700,68 Q740,68 740,170 Q740,290 480,290" stroke="#f57c00" stroke-width="1.5" fill="none" stroke-opacity=".6" stroke-dasharray="5,3" marker-end="url(#arr)"/>
  <text x="745" y="175" fill="#f57c00" fill-opacity=".8" font-size="10" writing-mode="tb">↗ promote</text>
</svg>
*Split-view layout: the resizable side panel hosts AI chat threads while the main panel tracks the current URL — the two panels operate independently.*

---

# Opening and Closing

| Action | How |
|--------|-----|
| Open the side panel | Click the **Chat** icon in the top-right header |
| Close the side panel | Click the **X** button in the side panel header |
| Resize | Drag the splitter bar between the two panels |

> **State is preserved on close.** Collapsing the panel does not destroy its content. Re-opening restores your previous view — scroll position, ongoing chat, and any typed text are all intact.

The side panel state (open/closed, active thread, size, and position) is **persisted across page reloads** via browser localStorage.

---

# Side Panel Header

The header provides quick access to common actions, split across left and right zones.

**Left side — title area:**

| Icon | Action | Description |
|------|--------|-------------|
| **+** | New Thread | Starts a fresh AI chat thread |
| **&#8634;** | Resume Thread | Opens a searchable list of recent threads to pick up |
| Title | Display only | Shows the current thread name or "New Thread" |

**Right side — panel controls:**

| Icon | Action | Description |
|------|--------|-------------|
| **&#x2197;** | Open in main panel | Promotes the current thread to the main panel and closes the side panel |
| **X** | Close | Collapses the side panel |

---

# Chat Threads

## Starting a New Chat

Click **+** in the side panel header, or simply open the side panel when no thread is active. The input area offers:

- A Monaco text editor with `@` references to any mesh node
- An agent selector (Orchestrator, Executor, Navigator, and others)
- A model selector (claude-sonnet, claude-opus, and others)

Press Enter or click **Send**. A thread node is created automatically on the first message — no manual naming required.

## Resuming a Thread

Click **&#8634;** to open a searchable list of your recent threads. The list shows threads from your current namespace, sorted by last modified date. Click any thread to load it in the side panel without disturbing the main panel.

## Promoting to Main Panel

Click **&#x2197;** to move the current thread from the side panel into the full main content area. The side panel closes and the browser navigates to the thread's full-page view, which adds a larger header with context links, full scrollable message history, and an input area at the bottom.

> The full-page thread header is automatically hidden when the same thread is rendered inside the side panel — the panel's own compact header takes over so nothing is duplicated.

---

# Main Panel

The main panel occupies the full width when the side panel is closed, and shares space via the resizable splitter when it is open. Its content always reflects the current URL.

## Thread View in Main Panel

When a thread is opened in the main panel — either by direct navigation or by promotion from the side panel — it shows:

- A back link to the parent context
- The chat icon and thread title
- Full message history with scrolling
- An input area at the bottom

---

# Interaction Between Panels

The two panels are deliberately independent: navigating in one does not disrupt the other.

| Scenario | Behavior |
|----------|----------|
| Open side panel while a thread is active in the main panel | Thread moves to the side panel; main panel navigates to the thread's parent |
| Click "Open in main panel" in the side panel | Thread moves to the main panel; side panel closes |
| Navigate to a different page in the main panel | Side panel content is preserved independently |
| Page reload | Side panel reopens with the same thread if it was open |
| Resume a thread from the list | Thread loads in the side panel only; main panel is unaffected |

---

# Persisted State

The following side panel state is saved to browser localStorage and survives page reloads:

| State | Details |
|-------|---------|
| Open / closed | Panel visibility on next load |
| Content path | Which thread (or other content) is displayed |
| Position | Right or bottom attachment |
| Size | Width / height from drag resizing |
| Display title | The title shown in the panel header |

---

# Live Demo

The cell below renders a small mock of the side panel's control strip so you can explore the layout controls used to build it:

```csharp --render SidePanelDemo --show-code
MeshWeaver.Layout.Controls.Stack
    .WithView(MeshWeaver.Layout.Controls.Html("<b>Side Panel Header Controls</b>"))
    .WithView(
        MeshWeaver.Layout.Controls.Stack
            .WithView(MeshWeaver.Layout.Controls.Button("+ New Thread"))
            .WithView(MeshWeaver.Layout.Controls.Button("↺ Resume"))
    )
    .WithView(MeshWeaver.Layout.Controls.Html("<hr/>"))
    .WithView(MeshWeaver.Layout.Controls.Html("<i>Main panel and side panel share space via a resizable splitter.</i>"))
```
