---
Name: Each AI agent appears once in the chat picker
Category: Fix
Description: Agents shipped by more than one layer — the platform, a package and your own copy — were listed two or three times in the chat agent dropdown.
Icon: Bot
Order: -20260825
---

# Each AI agent appears once in the chat picker

The agent dropdown in AI chat listed some agents twice or three times — the Tools Reference agent
appeared twice, and most built-in agents once for the platform, once for the package that ships them
and once for the copy installed into your own space. Each agent is now listed a single time, and the
entry you get is the most specific one available: your own copy if you have it, otherwise the one
from the space you are working in, otherwise the package's, otherwise the platform default.
