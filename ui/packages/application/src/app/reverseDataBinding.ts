import { distinctUntilChanged, from, map, merge, Observable, skip, Subscription } from "rxjs";
import { RootState } from "./store";
import { configureStore, Dispatch } from "@reduxjs/toolkit";
import { LayoutArea } from "../contract/LayoutArea";
import { isEqual, keys } from "lodash";
import { JsonPatch, WorkspaceReference } from "@open-smc/data/src/data.contract";
import { jsonPatch, workspaceReducer } from "@open-smc/data/src/workspace";
import { Binding, isBinding } from "../contract/Binding";
import { pickBy } from "lodash-es";
import { selectAll } from "@open-smc/data/src/selectAll";
import { effect } from "./effect";
import { walk } from 'walkjs';
import { selectByPath } from "@open-smc/data/src/selectByPath";
import { JSONPath } from "jsonpath-plus";

export const reverseDataBinding = (
    ui$: Observable<RootState>,
    data$: Observable<unknown>,
    dataDispatch: Dispatch
) =>
    (layoutArea: LayoutArea) => {
        const {control} = layoutArea;

        if (control) {
            const {dataContext, ...props} = control;

            const bindings = pickBy(props, isBinding);

            return data$
                .pipe(map(selectAll(dataContext)))
                .pipe(distinctUntilChanged(isEqual))
                .pipe(
                    effect(
                        dataContextState => {
                            if (dataContextState) {
                                const dataContextWorkspace =
                                    configureStore({
                                        reducer: workspaceReducer,
                                        preloadedState: dataContextState,
                                        devTools: {
                                            name: layoutArea.id
                                        }
                                    });

                                const subscription = new Subscription();

                                subscription.add(
                                    from(dataContextWorkspace)
                                        .pipe(dataPatch(dataContext))
                                        .subscribe(dataDispatch)
                                );

                                subscription.add(
                                    ui$
                                        .pipe(dataContextPatch(layoutArea.id, bindings))
                                        .subscribe(dataContextWorkspace.dispatch)
                                );

                                return subscription;
                            }
                        }
                    )
                )
                .subscribe();
        }
    };

const dataContextPatch = (layoutAreaId: string, bindings: Record<string, Binding>) =>
    (source: Observable<RootState>) =>
        merge(
            ...keys(bindings)
                .map(
                    key =>
                        source
                            .pipe(map(ui => ui.areas[layoutAreaId].control.props[key]))
                            .pipe(distinctUntilChanged(isEqual))
                            .pipe(skip(1))
                            .pipe(map(bindingToJsonPatchAction(bindings[key])))
                )
        )

const bindingToJsonPatchAction = (binding: Binding) =>
    (value: unknown) =>
        jsonPatch(
            new JsonPatch(
                [
                    {
                        op: "replace",
                        path: toPointer(binding.path),
                        value
                    }
                ]
            )
        );

const dataPatch = (dataContext: unknown) =>
    (source: Observable<unknown>) =>
        merge(
            ...extractReferences(dataContext)
                .map(
                    ({path, reference}) =>
                        source
                            .pipe(map(selectByPath(path)))
                            .pipe(distinctUntilChanged(isEqual))
                            .pipe(skip(1))
                            .pipe(map(referenceToJsonPatchAction(reference)))
                )
        );

const extractReferences = (dataContext: unknown) => {
    const references: {
        path: string,
        reference: WorkspaceReference
    }[] = [];

    walk(
        dataContext,
        {
            onVisit: {
                filters: node => node.val instanceof WorkspaceReference,
                callback:
                    node =>
                        references.push(
                            {
                                path: node.getPath(),
                                reference: node.val
                            }
                        )
            }
        }
    );

    return references;
}

const referenceToJsonPatchAction = (reference: WorkspaceReference) =>
    (value: unknown) =>
        jsonPatch(
            new JsonPatch(
                [
                    {
                        op: "replace",
                        path: toPointer(reference.toJsonPath()),
                        value
                    }
                ]
            )
        );

// JsonPath to JsonPointer e.g. "$.obj.property" => "/obj/property"
const toPointer = (jsonPath: string) =>
    JSONPath.toPointer(
        JSONPath.toPathArray(jsonPath)
    );
