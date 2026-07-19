import { describe, expect, it } from "vitest";
import { areaErrorMessage } from "./areaError";

// The RN-specific mapping of a faulted area subscription to a friendly notice. The classification
// itself is covered in @meshweaver/react (accessError.test); this pins the mobile message choice.
describe("areaErrorMessage", () => {
  it("is a friendly access message for a denial", () => {
    expect(areaErrorMessage("Access denied: user 'u' lacks Read permission on 'Course/Lesson'")).toBe(
      "You don’t have access to this view.",
    );
  });

  it("is a graceful message for a gone node (never the raw routing diagnostic)", () => {
    expect(
      areaErrorMessage("No node found at 'u/_Activity/markdown-abc'. Closest ancestor is 'u' (remainder='…')."),
    ).toBe("This view is no longer available.");
  });

  it("is a generic message for any other fault", () => {
    expect(areaErrorMessage("the area stream closed before delivering a snapshot")).toBe(
      "This view could not be loaded.",
    );
  });
});
