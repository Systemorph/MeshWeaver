---
Name: Side Panel & Main Panel
Category: Documentation
Description: How the side panel and main panel work together for multitasking, chat, and navigation
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="2"/><line x1="15" y1="3" x2="15" y2="21"/></svg>
---

The portal uses a split-view layout with a **main panel** and a collapsible **side panel**. The side panel is primarily used for AI chat threads but can display any content. The main panel shows the current page content.

# Opening and Closing

| Action | How |
|--------|-----|
| Open side panel | Click the **Chat** icon in the top-right header |
| Close side panel | Click the **X** button in the side panel header |
| Resize | Drag the splitter bar between the two panels |

When you close the side panel, it **collapses** rather than destroying its content. Re-opening restores your previous view including scroll position, ongoing chat, and typed text.

The side panel state (open/closed, content, size) is **persisted across page reloads** via browser localStorage.

---

# Side Panel Header

The header contains action buttons on the left and right:

**Left side (title area):**

| Icon | Action | Description |
|------|--------|-------------|
| **+** | New Thread | Starts a fresh AI chat thread |
| **&#8634;** | Resume Thread | Shows a searchable list of recent threads to resume |
| Title | Display only | Shows the current thread name or "New Thread" |

**Right side (actions):**

| Icon | Action | Description |
|------|--------|-------------|
| **&#x2197;** | Open in main panel | Moves the current thread to the main panel and closes the side panel |
| **X** | Close | Collapses the side panel |

---

# Chat Threads

## Starting a New Chat

Click **+** in the side panel header or open the side panel when no thread is active. You'll see:

- A text input with Monaco editor (supports `@` references to nodes)
- Agent selector (Orchestrator, Executor, Navigator, etc.)
- Model selector (claude-sonnet, claude-opus, etc.)

Type a message and press Enter or click Send. A thread is created automatically on first message.

## Resuming a Thread

Click the **&#8634;** (resume) button to see a searchable list of your recent threads. The list shows threads from your current namespace, sorted by last modified date. Click any thread to load it in the side panel.

## Promoting to Main Panel

Click the **&#x2197;** (maximize) button to move the current thread out of the side panel into the main content area. The side panel closes and the browser navigates to the thread's full-page view, which includes a larger header with context links.

---

# Main Panel

The main panel shows the current page content based on the URL. It always occupies the full width when the side panel is closed, and shares space via the resizable splitter when the side panel is open.

## Thread View in Main Panel

When a thread is viewed in the main panel (via direct navigation or promotion from the side panel), it shows a full header with:

- Back link to the parent context
- Chat icon and thread title
- Full message history with scrolling
- Input area at the bottom

This header is automatically hidden when the same thread is rendered inside the side panel, since the side panel has its own compact header.

---

# Interaction Between Panels

| Scenario | Behavior |
|----------|----------|
| Open side panel while viewing a thread in main panel | Thread moves to side panel; main panel navigates to the thread's parent |
| Click "Open in main panel" in side panel | Thread moves to main panel; side panel closes |
| Navigate to a different page in main panel | Side panel content is preserved independently |
| Page reload | Side panel reopens with the same thread if it was open |
| Resume a thread from the list | Thread loads in the side panel only (main panel is not affected) |

---

# Persistence

The following side panel state is saved to browser localStorage and survives page reloads:

- Whether the panel is open or closed
- The current content path (which thread is displayed)
- Panel position (right or bottom)
- Panel size (width/height from drag resizing)
- Display title
