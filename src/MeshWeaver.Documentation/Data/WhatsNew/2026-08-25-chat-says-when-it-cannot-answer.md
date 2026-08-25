---
Name: Chat tells you when it cannot answer, instead of guessing
Category: Fix
Description: A round that could not load your earlier messages used to answer anyway as if the thread were new; provider refusals like an exhausted budget now read as a plain sentence instead of a raw error dump.
Icon: Chat
Order: -20260825
---

# Chat tells you when it cannot answer, instead of guessing

Two things could go wrong in an AI chat round without the thread ever saying so.

**Losing your earlier messages, silently.** When the conversation's prior turns could not be loaded
in time, the round carried on regardless — the agent saw only your newest message and answered as
though the thread had just been created. Nothing on screen said anything had gone missing, so a long
triage or research thread could quietly repeat work or contradict what it had already established,
and the round still finished marked as successful. A round that cannot read the conversation now
**stops and says so**, and invites you to submit again; it no longer answers on a blank slate.

**Unreadable provider errors.** When the language-model provider refused a round — the account out
of credit, or a model that no longer exists at the configured endpoint — the thread showed the raw
error the provider's client library produced (`HTTP 402 (: )` followed by an English paragraph about
token budgets). That text was untranslated and mostly meaningless unless you administer the
deployment. Those two refusals now read as a plain, translated sentence naming the model and what to
do about it — top the provider account up, or check the model id in Settings → Language Models. The
full technical detail still goes to the logs for whoever operates the portal.
