---
Name: The licence lives on the instance, and every package fetch checks it
Category: Feature
Description: A registered instance carries its plan on its own record — free by default, promoted by a global admin in one field — and the registry decides every package it serves against that plan; instances authenticate with a short-lived JWT instead of their durable key on every call
Icon: Sparkle
Order: -20260830
---

# The licence lives on the instance, and every package fetch checks it

Every registered MeshWeaver instance now carries its **plan** on its own record. A new instance starts on the free plan (a registration key minted for a plan puts it on that plan), and a global admin raises it in the Instance grants settings tab with one field — no grant strings to edit. The registry decides every package listing and every bundle download against that plan and the package's declared tier: a free instance gets the free and untiered packages of the sources it is granted, and nothing above. Grant entries keep saying *which* sources and packages an instance may see; a plan on an entry can only cap the instance's plan for that source, never raise it. A promotion takes effect on the very next request.

Instances also stop presenting their durable key on every call. They exchange it once for a short-lived, standard JWT and send that; the key is used only to obtain the next token. An older registry that does not issue tokens still accepts the key directly.
