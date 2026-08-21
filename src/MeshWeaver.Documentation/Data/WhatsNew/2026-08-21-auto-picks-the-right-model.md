---
Name: Auto picks the right model for the job
Category: Feature
Description: The Auto model selection now looks at what you asked and routes the round to the most suitable model, instead of always using one fixed default.
Icon: Sparkle
Order: -20260821
---

# Auto picks the right model for the job

Until now, choosing **Auto** as the model always ran your thread on one fixed default. Now Auto
reads your request first: a quick routing step picks the most suitable of the available models —
a stronger model for complex reasoning or coding work, a faster one for short and simple asks —
and the round runs on that choice.

The routing is careful by design: if it cannot decide quickly, your round simply runs on the
familiar default, so Auto is never slower or less reliable than before. The chosen model is
recorded on the response, so you can always see which model served a round.
