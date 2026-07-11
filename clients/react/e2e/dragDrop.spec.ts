import { test, expect } from "@playwright/test";

test.describe("Draggable / DropTarget (React web demo)", () => {
  test.beforeEach(async ({ page }) => {
    await page.goto("/");
    await expect(page.locator('[data-draggable="card-1"]')).toBeVisible();
    await expect(page.locator("[data-drop-target]")).toBeVisible();
  });

  test("dropping the card posts a drop event carrying its payload", async ({ page }) => {
    // Drive a real HTML5 drag: dispatch the drag sequence with a shared DataTransfer so React's
    // onDragStart/onDrop fire (Playwright's dragTo does not reliably trigger native HTML5 DnD).
    await page.evaluate(() => {
      const src = document.querySelector('[data-draggable="card-1"]')!;
      const dst = document.querySelector("[data-drop-target]")!;
      const dataTransfer = new DataTransfer();
      const fire = (el: Element, type: string) =>
        el.dispatchEvent(new DragEvent(type, { bubbles: true, cancelable: true, dataTransfer }));
      fire(src, "dragstart");
      fire(dst, "dragover");
      fire(dst, "drop");
      fire(src, "dragend");
    });

    // The demo's event log reflects emitted MeshEvents; the drop must appear with its payload
    // and be scoped to the drop target's area.
    const log = page.locator("pre");
    await expect(log).toContainText('"kind":"drop"');
    await expect(log).toContainText('"value":"card-1"');
    await expect(log).toContainText('"area":"dropZone"');
  });
});
