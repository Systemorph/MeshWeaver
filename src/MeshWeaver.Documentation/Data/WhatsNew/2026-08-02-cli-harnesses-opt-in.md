---
Name: Claude Code and GitHub Copilot are now opt-in per user
Category: Feature
Description: The CLI harnesses no longer appear in everyone's chat picker by default — install them from the Store when you want them; uninstalling removes them again.
Icon: Sparkle
Order: -20260802
---

# Claude Code and GitHub Copilot are now opt-in per user

The Claude Code and GitHub Copilot execution harnesses require a personal subscription login — offering them to every user by default only produced confusing "Not logged in" dead ends for people who had never chosen them.

They are now install-gated per user:

- **By default**, the chat's harness picker offers the native MeshWeaver harness only. Nobody lands on a CLI harness without asking for it.
- **Opting in** is a Store install: installing the Claude Code (or GitHub Copilot) plugin places the harness in *your* picker only, ready for `/login`.
- **Uninstalling** removes it again — including for threads that had already selected it, which fall back gracefully to the standard agent path instead of erroring.

Existing threads or composers still pointing at a CLI harness they no longer have simply run on the default harness; nothing breaks.
