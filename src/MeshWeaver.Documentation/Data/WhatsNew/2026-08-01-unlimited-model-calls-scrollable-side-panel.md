---
Name: Long agent responses no longer time out, and the side panel scrolls content
Category: Fix
Description: Model calls are no longer cut off after 100 seconds, and node content shown in the side panel is now scrollable.
Icon: Sparkle
Order: -20260801
---

# Long agent responses no longer time out, and the side panel scrolls content

Agent rounds using models that think or generate for a long time (for example DeepSeek reasoning models) used to fail mid-answer with a timeout error after 100 seconds. That limit came from the underlying SDK defaults, not from anything you configured. Model calls now run without a transport time limit across all providers — a round ends when it finishes, when you cancel it, or at the platform's safety backstop.

Separately, when a thread shows a node's content in the side panel, long content was cut off at the bottom with no way to scroll. The side panel now scrolls its content just like the main view.
