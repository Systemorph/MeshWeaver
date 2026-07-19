// The "no access ⇒ redirect here" decision LiveArea makes — the portal-next twin of the Blazor
// NamedAreaView redirect (combining the shared classifier + isSafeRedirect loop-guard). Pure, so it
// is unit-tested without rendering the client boundary.
import { describe, expect, it } from "vitest";
import { deniedRedirectTarget } from "../src/client/live";

const denialFor = (path: string) => `Access denied: user 'u' lacks Read permission on '${path}'`;

describe("deniedRedirectTarget", () => {
  it("redirects a server-detected denial to the configured cover", () => {
    expect(
      deniedRedirectTarget({
        initialDenied: true,
        address: "Course/Lesson1",
        redirectOnDenied: "Course/Cover",
      }),
    ).toBe("/Course/Cover");
  });

  it("redirects a live access-denied fault to the configured cover", () => {
    expect(
      deniedRedirectTarget({
        streamError: denialFor("Course/Lesson1"),
        address: "Course/Lesson1",
        redirectOnDenied: "Course/Cover",
      }),
    ).toBe("/Course/Cover");
  });

  it("normalizes a leading slash on the target to a single in-app route", () => {
    expect(
      deniedRedirectTarget({ initialDenied: true, address: "Course/Lesson1", redirectOnDenied: "/Course/Cover" }),
    ).toBe("/Course/Cover");
  });

  it("does NOT redirect when denied but no target is configured (⇒ honest error)", () => {
    expect(deniedRedirectTarget({ initialDenied: true, address: "Course/Lesson1", redirectOnDenied: null })).toBeNull();
  });

  it("does NOT redirect when the target would loop (target itself / a node under it denied)", () => {
    expect(
      deniedRedirectTarget({ initialDenied: true, address: "Course/Cover", redirectOnDenied: "Course/Cover" }),
    ).toBeNull();
    expect(
      deniedRedirectTarget({ initialDenied: true, address: "Course/Cover/Sub", redirectOnDenied: "Course/Cover" }),
    ).toBeNull();
  });

  it("does NOT redirect when the error is not an access denial (a plain failure / node gone)", () => {
    expect(
      deniedRedirectTarget({
        streamError: "No node found at 'Course/Lesson1'.",
        address: "Course/Lesson1",
        redirectOnDenied: "Course/Cover",
      }),
    ).toBeNull();
    expect(
      deniedRedirectTarget({
        streamError: "the area stream closed before delivering a snapshot",
        address: "Course/Lesson1",
        redirectOnDenied: "Course/Cover",
      }),
    ).toBeNull();
  });

  it("does NOT redirect when there is no denial at all", () => {
    expect(deniedRedirectTarget({ address: "Course/Lesson1", redirectOnDenied: "Course/Cover" })).toBeNull();
  });
});
