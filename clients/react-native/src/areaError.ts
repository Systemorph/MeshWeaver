import { classifyAreaError } from "@meshweaver/react/core";

/**
 * The human message for an area-subscription fault, classified via the shared AreaErrorClassifier
 * twin. A mobile client has no login / paywall-redirect flow, so it just informs — the RN analog of
 * the Blazor NamedAreaView placeholder: a denial or a gone node gets a friendly message, never the
 * raw framework diagnostic ("No node found at 'u/_Activity/…'. Closest ancestor is …").
 */
export function areaErrorMessage(message: string): string {
  const info = classifyAreaError(message);
  return info?.kind === "access-denied"
    ? "You don’t have access to this view."
    : info?.kind === "not-found"
      ? "This view is no longer available."
      : "This view could not be loaded.";
}
