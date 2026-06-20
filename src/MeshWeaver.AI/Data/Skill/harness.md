---
nodeType: Skill
name: /harness
description: Switch the harness (runtime) for subsequent messages
icon: Sparkle
category: Skills
order: 3
action:
  kind: Pick
  query: "namespace:Harness nodeType:Harness sort:order"
  field: harness
  title: Choose a harness
---
