---
Description: Remove the unnecessary horizontal scrollbar shown in the Recently Viewed section of the MeshWeaver UI title page
---

## Summary
Remove the unnecessary horizontal scrollbar shown in the Recently Viewed section on the MeshWeaver title page. The update should ensure recently viewed items fit cleanly within the available layout, preserve readability, and avoid introducing clipping or overflow regressions across common viewport sizes.

## Motivation
The horizontal scrollbar makes the title page feel visually broken and suggests the layout is unstable even when the content is otherwise usable. Removing this overflow improves first impressions, reduces friction when navigating recent items, and makes the MeshWeaver home experience feel more polished and intentional.

## Detailed Design
Investigate the Recently Viewed section on the title page to identify the source of horizontal overflow. Likely causes include a child container using width rules larger than its parent, missing overflow constraints, non-wrapping content, grid or flex settings with insufficient shrink behavior, or padding and gap calculations that exceed the available width.

Update the section so that the layout respects the width of its parent container across typical desktop and smaller application window sizes. Prefer a fix that addresses the underlying sizing behavior rather than simply hiding overflow globally. If item cards or rows currently force a minimum width larger than the viewport or content area, adjust flex, grid, min-width, white-space, or box-sizing behavior so the section can shrink gracefully.

Validate the result in the title page context with realistic recently viewed entries, including long item names. Ensure the fix does not regress scrolling behavior elsewhere, does not truncate critical information unexpectedly, and does not interfere with intended responsive behavior.

## Acceptance Criteria
- [ ] The Recently Viewed section on the MeshWeaver title page no longer shows a horizontal scrollbar during normal use on common application window sizes.
- [ ] Recently viewed items remain readable and accessible after the layout fix, including entries with longer titles.
- [ ] The implementation fixes the source of horizontal overflow without introducing clipping, broken alignment, or unintended overflow changes in neighboring title page sections.

## Dependencies
No known hard dependencies.

## Open Questions
- Is the overflow caused by the Recently Viewed container itself, an individual card component, or a shared layout primitive used elsewhere?
- Should the final behavior wrap items, resize cards, or switch to a different responsive layout at narrower widths?
- Are there any title page width breakpoints or design constraints that should be preserved explicitly?

---
GitHub Issue: https://github.com/Systemorph/MeshWeaver/issues/76
