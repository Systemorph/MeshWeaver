import { LayoutArea } from "../contract/LayoutArea";
import { isEqualWith } from "lodash-es";
import { isOfType } from "../contract/ofType";

export const ignoreNestedAreas = (previous: LayoutArea, current: LayoutArea) =>
    isEqualWith(
        previous,
        current,
        (value, other, indexOrKey, stack) => {
            if (isOfType(value, LayoutArea) && isOfType(other, LayoutArea) && indexOrKey) {
                return value.id === other.id;
            }
        }
    );