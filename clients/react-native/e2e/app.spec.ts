import { test, expect } from "@playwright/test";

// The RN app (offline StaticAreaSource sample) rendered through the native leaf pack + the shared core,
// running as react-native-web. Proves the SAME UiControl tree renders on the RN pack end-to-end in a real
// browser — a level above the vitest render tests (which mock react-native): here react-native-web maps
// View→div, Text→div, TextInput→input, Switch→checkbox, so we assert the actual rendered output.
test.describe("React Native app renders the sample area", () => {
  test.beforeEach(async ({ page }) => {
    await page.goto("/");
    // The app is a client-rendered SPA — wait for the first control to mount.
    await expect(page.getByText("MeshWeaver on React Native")).toBeVisible();
  });

  test("renders Label/Markdown text (header + intro)", async ({ page }) => {
    await expect(page.getByText("MeshWeaver on React Native")).toBeVisible();
    await expect(page.getByText(/native leaves/)).toBeVisible();
    await expect(page.getByText(/Rendered by the RN leaf pack/)).toBeVisible();
  });

  test("binds the TextField to /data/name", async ({ page }) => {
    // Not input.first() — the shell's topbar search box is also an <input>; assert that the
    // content pane's TextField (some input on the page) carries the bound /data/name value.
    await expect
      .poll(async () =>
        page.locator("input").evaluateAll((els) => els.map((e) => (e as HTMLInputElement).value)),
      )
      .toContain("Ada Lovelace");
  });

  test("renders the CheckBox as a bound Switch (checked)", async ({ page }) => {
    // react-native-web Switch renders a checkbox input reflecting /data/active = true.
    const checkbox = page.locator('input[type="checkbox"]').first();
    await expect(checkbox).toBeChecked();
  });

  test("renders the DataGrid with column titles + every bound row cell", async ({ page }) => {
    await expect(page.getByText("Account")).toBeVisible(); // column title
    await expect(page.getByText("Amount")).toBeVisible();
    await expect(page.getByText("ACME")).toBeVisible(); // /data/rows[0].name
    await expect(page.getByText("124000")).toBeVisible(); // /data/rows[0].amount
    await expect(page.getByText("Northwind")).toBeVisible(); // /data/rows[1].name
  });

  test("renders Button + Badge, and hits no Unsupported fallback", async ({ page }) => {
    await expect(page.getByText("Save")).toBeVisible(); // Button
    await expect(page.getByText("Green")).toBeVisible(); // Badge
    await expect(page.getByText(/Unsupported/)).toHaveCount(0);
  });
});
