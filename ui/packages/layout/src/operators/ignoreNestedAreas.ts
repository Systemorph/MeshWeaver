import { LayoutArea } from "../contract/LayoutArea";
import { isEqualWith } from "lodash-es";

export const ignoreNestedAreas = (previous: LayoutArea, current: LayoutArea) =>
    isEqualWith(
        previous,
        current,
        (value, other, indexOrKey, stack) => {
            if (value instanceof LayoutArea && other instanceof LayoutArea && indexOrKey !== undefined) {
                return value.id === other.id;
            }
        }
    );