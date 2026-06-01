---
Name: Side Panel & Main Panel
Category: Documentation
Description: How the side panel and main panel work together for multitasking, AI chat, and contextual navigation without losing your place
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="2"/><line x1="15" y1="3" x2="15" y2="21"/></svg>
---

The portal uses a **split-view layout**: a resizable **side panel** that slides in from the right, and a **main panel** that always shows the current page. The side panel is designed for AI chat threads, but it can host any content — and it remembers exactly where you left off.

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
