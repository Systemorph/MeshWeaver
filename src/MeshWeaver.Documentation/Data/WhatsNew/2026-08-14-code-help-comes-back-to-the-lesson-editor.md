---
Name: Code help comes back to the lesson editor
Category: Fix
Description: An example cell offered only alphabetical guesses scraped from its own text, and no error underlines — the editor was asking the wrong place for the compiler that answers those questions.
Icon: Lightbulb
Order: -20260814
---

# Code help comes back to the lesson editor

An example cell in a lesson is a real code editor: it should complete what you type from the actual
types in scope, and underline mistakes as you make them.

Instead it offered a list of words scraped out of the cell's own comments and strings, in
alphabetical order, and never underlined anything. That is the editor's own fallback for when
nothing better is available — and nothing better was arriving, because the editor asked the wrong
place for the service that provides it. The request went to the page's own set of services rather
than to the mesh, where the compiler-backed one actually lives, and came back empty.

Nothing reported it. Completions and error underlines are both switched off by the same empty
answer, so the only visible sign was suggestions that felt oddly dumb.

The editor now asks the mesh, and says so in the log if the service really is missing — so "no code
help" can never again look the same as "no problems found".
