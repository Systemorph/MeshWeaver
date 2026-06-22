---
nodeType: Skill
name: /model
description: Switch the AI model for subsequent messages
icon: Sparkle
category: Skills
order: 2
action:
  kind: Pick
  query: "namespace:Admin/Provider nodeType:LanguageModel scope:descendants sort:order"
  field: modelName
  title: Choose a model
---
