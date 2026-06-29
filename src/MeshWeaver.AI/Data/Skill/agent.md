---
nodeType: Skill
name: /agent
description: Switch the agent for subsequent messages
icon: Sparkle
category: Skills
order: 1
action:
  kind: Pick
  query: "namespace:Agent nodeType:Agent -content.modelTier:utility sort:order"
  field: agentName
  title: Choose an agent
---
