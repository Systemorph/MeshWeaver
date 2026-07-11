import { test, expect } from "@playwright/test";

// The RN Draggable/DropTarget rendered through react-native-web (View→div). On drop the offline demo
// source (createSampleSource) reflects the payload into the drop zone's label, so the interaction is
// observable end to end in a real browser — the level above the vitest render test.
test.describe("React Native drag & drop (react-native-web)", () => {
  test.beforeEach(async ({ page }) => {
    await page.goto("/");
    await expect(page.getByText("Drag me")).toBeVisible();
    await expect(page.getByText("Drop here")).toBeVisible();
  });

  test("dropping the card reflects its payload in the drop zone", async ({ page }) => {
    // Drive a real HTML5 drag sequence with a shared DataTransfer (Playwright's dragTo does not
    // reliably trigger native HTML5 DnD). The RN wrapper's DOM listeners handle these events.
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

    await expect(page.getByText("Dropped: card-1")).toBeVisible();
  });
});
