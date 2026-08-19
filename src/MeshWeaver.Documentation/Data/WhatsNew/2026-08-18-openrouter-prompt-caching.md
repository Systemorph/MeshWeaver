---
Name: OpenRouter threads now cache their prompts
Category: Feature
Description: Chat requests through OpenRouter now opt into prompt caching, so long agent threads on models like Claude re-read their context at the cached rate instead of paying full input price every round — and the cached share shows up in the thread's token chip.
Icon: TopSpeed
Order: -20260818
---

# OpenRouter threads now cache their prompts

Every round of an agent thread re-sends the same large prefix to the model: the
agent's instructions, its tool documentation, and the whole conversation so far.
Some providers cache that prefix on their own, but several of the models you can
run through OpenRouter — Anthropic's Claude among them — only cache when the
request explicitly asks for it. Ours never did, so those threads paid full input
price for the same prefix on every single round.

Now every chat request sent through an OpenRouter provider opts into prompt
caching. After the first round, the model re-reads the unchanged prefix at the
provider's reduced cache rate — roughly a tenth of the normal input price on
Claude — which adds up quickly on long, tool-heavy threads. Providers that cache
automatically are unaffected, and short prompts below the provider's minimum
cacheable size simply keep behaving as before.

You can see it working: the thread's token chip and usage breakdown now show how
much of the input was served from cache.
