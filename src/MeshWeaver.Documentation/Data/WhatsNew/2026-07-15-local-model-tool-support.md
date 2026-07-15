---
Name: Local models that don't support tools now just work
Category: What's New
Description: Ollama/OpenAI-compatible models are checked for tool support so a completion-only model no longer errors the chat.
Icon: Sparkle
---

# Local models that don't support tools now just work

When you bring your own local model through an Ollama / OpenAI-compatible endpoint, the assistant now checks whether that model supports tool calling before it uses it. A completion-only or roleplay model (which used to fail the whole chat with a *"does not support tools"* error) is detected automatically and sent a plain chat request instead — so it works as a chat model rather than breaking the round.

This check runs both when you add a provider in **Settings → Language Models** and, for models configured on the server, shortly after startup. Tool-capable models are unaffected and keep full tool access.

Running a local model and seeing a *context size* error instead? Local runtimes load with a small default context window — raise it on the host (for Ollama, set `OLLAMA_CONTEXT_LENGTH`) so the full prompt fits.
