---
Name: A suggested prompt is now something you can run
Category: Feature
Description: A ```prompt fence on a page renders as a composer pre-filled with the author's prompt — edit it in place and Submit starts a real agent thread, full page.
Icon: Chat
Order: -20260902
---

# A suggested prompt is now something you can run

Course pages have always suggested prompts to try. They were written as fenced blocks:

````
```prompt
Show two versions of the same movement report: one with a single "unexplained"
balancing line, one with the movement broken out.
```
````

…and they arrived as *text about a prompt*. You could read it, select it, paste it somewhere else
— but not change a word of it where it stood, and not run it. The obvious question was the one that
kept getting asked: **why can't I just edit this and send it?**

Now you can. A `prompt` fence renders as a **composer, pre-filled with the author's prompt**. Edit
it in place — narrow it to your own data, change the question, add a constraint — and **Submit**
starts a real agent thread seeded with what you actually typed, and opens that thread **full page**.
The lesson you started from travels with it as the thread's context, so the agent knows which page
the question came from.

Nothing needs to change in the content. Every page that already ships a `prompt` fence lights up as
it is.

## Where it stays plain

A prompt fence still renders as an ordinary, readable fenced block wherever a composer cannot be
put on the page — an exported document, a page with no owning node behind it. That is deliberate:
the prompt never disappears just because the surface reading it cannot offer the composer.

## For authors

Write the fence, nothing else:

````
```prompt
Summarise this month's movement report and flag anything unexplained.
```
````

The full fence dialect is in [Interactive Markdown](/Doc/DataMesh/InteractiveMarkdown); how an
interactive fence is built is in [Markdown Fence Extensions](/Doc/Architecture/MarkdownFenceExtensions).
