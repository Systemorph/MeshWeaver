import { Store } from "@reduxjs/toolkit";
import {
    distinctUntilChanged,
    from,
    map,
    mergeMap,
    Observable,
    of,
    pairwise,
    pipe,
    startWith,
    switchMap,
    tap
} from "rxjs";
import { get, isEqualWith } from "lodash-es";
import { LayoutArea } from "../contract/LayoutArea";
import { Control } from "../contract/controls/Control";
import { isOfType } from "../contract/ofType";
import { LayoutStackControl } from "../contract/controls/LayoutStackControl";
import { ControlModel, LayoutAreaModel, removeArea, setArea } from "./store";
import { isEmpty } from "lodash";
import { withTeardownBefore } from "./withTeardownBefore";

export type PropertyPath = (string | number)[];

// subscribe to root area
// nestedSubscriptions = combine child subscriptions for each nested area
// call setArea
// return () => removeArea

// for each layoutArea:
// - do cleanup (cleanup children, call removeArea)
// - render children
// - call setArea

// // subscribe to data-bound props, on change update data context
// from(store)
//     .pipe(map(state => state.areas["123"].control.props["data"]))
//     .pipe(distinctUntilChanged())
//     .subscribe(value => {
//         // update dataContext
//     });

export function fromLayoutArea(layoutStore: Store, path: PropertyPath, store: Store) {
    return from(layoutStore)
        .pipe(map(state => isEmpty(path) ? state : get(state, path) as LayoutArea))
        .pipe(
            distinctUntilChanged((previous, current) =>
                isEqualWith(previous, current, customizer))
        )
        // .pipe(withPreviousValue())
        .pipe(switchMap(current => {
            if (!current) {
                return of();
            }

            const [layoutAreaModel, nestedAreaPaths] = getLayoutAreaModel(current);

            const nestedSubscriptions =
                nestedAreaPaths.map(nestedAreaPath =>
                    fromLayoutArea(layoutStore, [...path, ...nestedAreaPath], store)
                        .subscribe()
                );

            return new Observable<LayoutAreaModel>(
                subscriber => subscriber.next(layoutAreaModel)
            ).pipe(withTeardownBefore(() => {
                store.dispatch(removeArea(current.id));
                nestedSubscriptions.forEach(subscription => subscription.unsubscribe());
            }));
        }))
        .pipe(
            tap(layoutAreaModel => {
                if (layoutAreaModel) {
                    store.dispatch(setArea(layoutAreaModel));
                }
            })
        );
}

function withPreviousValue<T>() {
    return pipe(
        startWith(undefined),
        pairwise<T>()
    );
}

// TODO: stack is empty (3/19/2024, akravets)
function customizer(value: any, other: any, indexOrKey: string | number | symbol) {
    if (isOfType(value, LayoutArea) && isOfType(other, LayoutArea) && indexOrKey === undefined) {
        return value.id === other.id;
    }
}

function getLayoutAreaModel(layoutArea: LayoutArea) {
    const {id, control, options, style} = layoutArea;

    const [controlModel, nestedAreaPaths] = getControlModel(control);

    const layoutAreaModel = {
        id,
        control: controlModel,
        options,
        style
    } as LayoutAreaModel;

    return [layoutAreaModel, nestedAreaPaths] as const;
}

function getControlModel(control: Control): [ControlModel, PropertyPath[]] {
    const {$type, dataContext, ...props} = control;

    const componentTypeName = $type.split(".").pop();

    if (isOfType(control, LayoutStackControl)) {
        const {areas, ...props} = control;

        const areaIds = areas.map(area => area.id);

        const model = {
            componentTypeName,
            props: {
                ...props,
                areaIds
            }
        };

        return [
            model,
            areaIds.map((areaId, index) => ["control", "areas", index])
        ] as const;
    }

    return [
        {
            componentTypeName,
            props: {
                ...control
            }
        },
        []
    ];
}