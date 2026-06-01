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
