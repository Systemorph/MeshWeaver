---
Name: You are told when a different model answered your round
Category: What's New
Description: A round that gets moved onto a fallback model now records the model that actually answered, keeps the requested one alongside it, and fails outright when no model can serve.
Icon: Sparkle
---

# You are told when a different model answered your round

When the model pinned on a thread has no usable credentials, the round is quietly
moved onto another one so your work keeps running. That part is unchanged — but
until now the record lied about it: the thread stored the model you *asked for*
while a different model did the answering. A failing round then blamed a model
that never ran, and the tokens were booked against it too.

Every round now records the model that **actually answered**. When that is not the
model you picked, the response also carries the requested one alongside it, so the
swap is visible on the thread itself — not only in the chat text. That matters for
rounds nobody is watching: a delegation sub-thread, a generator agent, a scheduled
run. Their result now carries the fact, and an operator sees a warning naming both
models in the log.

Token usage and cost follow the same truth: they are booked against the model that
served the round.

And when *no* model can serve a round at all — the pinned one has no credentials
and nothing else in the catalogue does either — the round now **fails**, with a
message naming the situation and pointing at Settings → Language Models. Before,
it ended as a success carrying a raw provider error, which anything reading the
result had no way to tell apart from a real answer.
