---
Description: "Bug: Typing a slash character in a chat prompt causes incorrect composer behavior"
---

## Summary
The chat composer currently behaves incorrectly when a prompt contains the slash character (`/`), which can interfere with normal typing, focus retention, command detection, or Enter-based submission. This feature defines a fix so prompts containing `/` are handled predictably as plain text unless an intentional slash-command interaction is explicitly activated.

## Motivation
Users often type paths, commands, URLs, API routes, and other text that naturally includes `/`. If the composer misinterprets slash input or disrupts normal keyboard flow, the chat experience feels unreliable and makes it harder for users to submit prompts confidently.

## Detailed Design
Investigate the chat composer input pipeline to determine whether `/` is being treated as a special control character by slash-command parsing, autocomplete logic, overlay activation, tokenization, or keyboard event routing. The fix should ensure that typing `/` does not unexpectedly blur the input, mutate the prompt text, disable sending, or reroute Enter handling unless the user is in a clearly defined and intentional command-selection flow.

Implementation should distinguish between plain-text prompt entry and any supported slash-command UX. If slash commands are intentionally supported, they must activate only under explicit and well-defined conditions, such as a leading slash in an empty composer or a recognized command-selection state.

## Acceptance Criteria
- [ ] Typing `/` into the chat composer does not cause unintended loss of focus.
- [ ] A prompt containing `/` can be submitted with Enter when the prompt is otherwise valid and no explicit command-selection action is in progress.
- [ ] The presence of `/` does not corrupt, remove, rewrite, or otherwise mutate the prompt text before submission.
- [ ] If slash-command suggestions or command UI are intentionally supported, they activate only under defined conditions and do not interfere with plain-text prompts containing `/`.
- [ ] Prompts containing `/` behave consistently for at least these cases: leading slash, slash in the middle of text, prompt ending with slash, and path-like strings such as `foo/bar` and `/api/test`.

## Dependencies
Related feature: `MeshWeaver/ChatComposerQuestionMarkFocusLoss`, which provides an analogous punctuation-related composer bug pattern.

## Open Questions
- Does the issue occur when `/` is the first character, the last character, or anywhere in the prompt?
- Is slash-command behavior currently intended in this composer, and if so, what exact trigger conditions are expected?
- Should plain text such as `/api/test`, `foo/bar`, or `Use / in this example` always submit untouched?

---
GitHub Issue: https://github.com/Systemorph/MeshWeaver/issues/70
