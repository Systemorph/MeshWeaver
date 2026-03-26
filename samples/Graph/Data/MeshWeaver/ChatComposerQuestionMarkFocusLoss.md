---
Description: "Bug: In the chat window, when a prompt ends with a question mark, focus leaves the input so pressing Enter does not submit"
---

## Summary
The chat composer currently loses keyboard focus when a prompt ends with a question mark, which prevents the Enter key from submitting the message. This bug breaks the normal chat workflow and forces users to click the send icon manually, creating an inconsistent and frustrating input experience.

## Motivation
Users expect chat input to behave consistently regardless of punctuation. Losing focus after typing a question mark makes the interface feel unreliable, slows down message sending, and disproportionately affects natural conversational use because many prompts are phrased as questions.

## Detailed Design
Investigate the chat composer input pipeline to identify why typing a trailing `?` causes focus to leave the active input control. The fix should ensure that punctuation, including `?`, does not trigger blur, focus transfer, disabled state changes, or interception by overlays, suggestion UI, or rich-text/input transforms that prevent Enter-based submission.

Implementation should verify the keyboard submission path from the active composer control through the Enter key handler, including standard keydown handling, composition/IME safeguards, and any logic that distinguishes Enter from Shift+Enter. If auxiliary UI is activated while typing, it must not steal focus from the composer unless explicitly intended and accessible, and Enter should still submit when the composer is in a valid sendable state.

## Acceptance Criteria
- [ ] When a user types a prompt ending with `?` in the chat input, the composer retains focus after the character is entered.
- [ ] Pressing Enter after a prompt ending with `?` submits the message exactly as it does for prompts ending with `.`, `!`, or no punctuation.
- [ ] Users are not required to click the send icon to submit a prompt solely because it ends with `?`.
- [ ] No suggestion popup, overlay, button state change, or other UI behavior triggered during typing may steal focus from the composer in this scenario.

## Dependencies
No direct existing feature/spec for this exact bug was found in the mesh.

## Open Questions
- Is the focus loss caused by the browser input element itself, a portal renderer behavior, or chat-specific enhancement logic layered on top of the input?
- Does the issue occur only when `?` is the final character, or also when `?` appears earlier in the prompt?
- Does the problem reproduce consistently across supported browsers and with IME/composition enabled?

---
GitHub Issue: https://github.com/Systemorph/MeshWeaver/issues/69
