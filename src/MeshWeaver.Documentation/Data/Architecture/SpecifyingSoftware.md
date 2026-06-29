---
NodeType: Markdown
Name: "Specifying Software"
Abstract: "Writing specification is still quite complex, and good tooling is difficult to find. In many cases, mockups are created but without any real functionality. We believe that specification must be written iteratively and as closely as possible to the implementation."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#283593'/><rect x='6' y='5' width='12' height='14' rx='1' fill='white'/><path d='M11 10l4 2-4 2z' fill='#283593'/></svg>"
VideoUrl: "https://www.youtube.com/embed/CtpgzjClS5c?si=jqOftd0uSGqbjFvS"
VideoDuration: "00:12:16"
VideoTitle: "Mastering Software Specifications"
VideoTagLine: "Create Interactive Specification"
VideoTranscript: "transcripts/Specifying Software.txt"
Thumbnail: "images/SpecifyingSoftware.jpeg"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Video"
  - "Specification"
---

Software specifications are hard to get right. Traditional approaches produce static mockups — polished documents that describe intent but offer no way to validate it. When the gap between specification and implementation widens, costly rework follows.

This article lays out a more effective approach: specifications that are **iterative, practical, and tightly coupled to the actual implementation**.
<svg viewBox="0 0 760 260" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arrowB" markerWidth="10" markerHeight="7" refX="9" refY="3.5" orient="auto">
      <polygon points="0 0, 10 3.5, 0 7" fill="#90a4ae"/>
    </marker>
    <marker id="arrowG" markerWidth="10" markerHeight="7" refX="9" refY="3.5" orient="auto">
      <polygon points="0 0, 10 3.5, 0 7" fill="#43a047"/>
    </marker>
  </defs>
  <text x="160" y="22" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="currentColor" fill-opacity="0.55">Static Approach</text>
  <rect x="30" y="34" width="120" height="44" rx="10" fill="#455a64"/>
  <text x="90" y="56" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">Specification</text>
  <text x="90" y="72" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#cfd8dc">(written once)</text>
  <line x1="150" y1="56" x2="196" y2="56" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arrowB)"/>
  <rect x="196" y="34" width="120" height="44" rx="10" fill="#455a64"/>
  <text x="256" y="56" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">Development</text>
  <text x="256" y="72" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#cfd8dc">(code drifts away)</text>
  <line x1="316" y1="56" x2="362" y2="56" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arrowB)"/>
  <rect x="362" y="34" width="120" height="44" rx="10" fill="#b71c1c"/>
  <text x="422" y="56" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">Rework</text>
  <text x="422" y="72" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ffcdd2">(costly divergence)</text>
  <line x1="0" y1="110" x2="760" y2="110" stroke="currentColor" stroke-opacity="0.15" stroke-width="1"/>
  <text x="420" y="132" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="currentColor" fill-opacity="0.55">Living Specification Approach</text>
  <rect x="30" y="148" width="130" height="52" rx="10" fill="#1565c0"/>
  <text x="95" y="170" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">Specification</text>
  <text x="95" y="186" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#bbdefb">iterative artefact</text>
  <line x1="160" y1="174" x2="206" y2="174" stroke="#43a047" stroke-width="2" marker-end="url(#arrowG)"/>
  <rect x="206" y="148" width="130" height="52" rx="10" fill="#2e7d32"/>
  <text x="271" y="170" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">Development</text>
  <text x="271" y="186" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#c8e6c9">code + examples</text>
  <line x1="336" y1="174" x2="382" y2="174" stroke="#43a047" stroke-width="2" marker-end="url(#arrowG)"/>
  <rect x="382" y="148" width="130" height="52" rx="10" fill="#e65100"/>
  <text x="447" y="170" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">Validation</text>
  <text x="447" y="186" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ffe0b2">executable tests</text>
  <line x1="447" y1="200" x2="447" y2="228" stroke="#43a047" stroke-width="2"/>
  <line x1="447" y1="228" x2="95" y2="228" stroke="#43a047" stroke-width="2"/>
  <line x1="95" y1="228" x2="95" y2="200" stroke="#43a047" stroke-width="2" marker-end="url(#arrowG)"/>
  <text x="271" y="248" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#43a047">feedback refines the spec continuously</text>
</svg>
*Static specs diverge silently; living specifications close the loop — validation feeds back into the spec on every iteration.*

## The Problem with Static Specs

Most spec processes share the same failure mode: a document is written once, reviewed in isolation, and then slowly diverges from reality as development proceeds. Mockups convey layout but not behavior. Written descriptions convey intent but not structure. Neither gives developers something they can act on directly.

The result is a translation tax paid on every feature — from spec to ticket, from ticket to code, and back again through rounds of review and correction.

## Specifications as Living Artefacts

A specification that stays useful throughout development has three properties:

| Property | What it means |
|---|---|
| **Iterative** | Updated continuously as feedback arrives and decisions evolve |
| **Practical** | Contains code snippets, architectural diagrams, and technical detail developers can act on |
| **Validated** | Close enough to the implementation that discrepancies surface early, not at release |

The goal is not a perfect document written once. It is a shared source of truth that grows alongside the codebase.

## Closing the Gap Between Design and Development

Useful specifications do more than describe *what* software should do — they explain *how* it should be built. That means including:

- **Code snippets** that illustrate patterns and API shapes
- **Architectural diagrams** that show how components relate
- **Executable examples** that can be tested and validated directly

When a specification includes runnable examples, it stops being a description and starts being a contract. Issues that would otherwise surface during integration review can be caught the moment the spec is written.

## Integrating Tooling Without Creating Bottlenecks

The right tooling makes the iterative loop cheap. Tools that support executable specifications allow teams to write specs that are continuously tested — so the specification and the implementation cannot silently diverge.

> The specification process should feel like part of development, not a separate tax on it. If maintaining the spec takes more effort than writing the code, the tooling is wrong.

Choosing tools that integrate naturally with the existing development workflow is essential. Friction in the spec process compounds across every feature and every sprint.

## Putting It Together

The practical upshot is straightforward:

1. Treat the specification as a first-class artefact in the repository — versioned alongside the code it describes.
2. Write specifications iteratively. Refine them as you learn.
3. Include technical detail: code, diagrams, executable examples.
4. Use tooling that validates the spec continuously so drift is detected early.

This approach reduces the risk of costly rework, keeps stakeholders aligned throughout development, and produces a final product that genuinely matches the original intent.
