import { Dispatch } from "@reduxjs/toolkit";
import { distinctUntilChanged, Observable, tap } from "rxjs";
import { LayoutArea } from "../contract/LayoutArea";
import { ignoreNestedAreas } from "./ignoreNestedAreas";
import { ControlModel, LayoutAreaModel, setArea } from "./store";
import { Control } from "../contract/controls/Control";
import { isOfType } from "../contract/ofType";
import { LayoutStackControl } from "../contract/controls/LayoutStackControl";
import { withPreviousValue } from "./withPreviousValue";

export const setNewArea = (dispatch: Dispatch, data$: Observable<any>) =>
    (source: Observable<LayoutArea>) =>
        source
            .pipe(distinctUntilChanged(ignoreNestedAreas))
            .pipe(
                tap(
                    layoutArea =>
                        layoutArea && dispatch(setArea(toLayoutAreaModel(layoutArea)))
                )
            )


export function toLayoutAreaModel(layoutArea: LayoutArea): LayoutAreaModel {
    const {id, control, options, style} = layoutArea;

    return {
        id,
        control: toControlModel(control),
        options,
        style
    }
}

function toControlModel(control: Control): ControlModel {
    const {$type, dataContext, ...props} = control;

    const componentTypeName = $type.split(".").pop();

    if (isOfType(control, LayoutStackControl)) {
        const {areas, ...props} = control;

        return {
            componentTypeName,
            props: {
                ...props,
                areaIds: areas.map(area => area.id)
            }
        }
    }

    return {
        componentTypeName,
        props
    }
}