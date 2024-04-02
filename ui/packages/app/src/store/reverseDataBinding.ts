import { distinctUntilChanged, from, map, merge, Observable, skip, Subscription, tap } from "rxjs";
import { AppState } from "./appStore";
import { configureStore, Dispatch } from "@reduxjs/toolkit";
import { LayoutArea } from "@open-smc/layout/src/contract/LayoutArea";
import { isEqual, keys } from "lodash";
import { JsonPatch, WorkspaceReference } from "@open-smc/data/src/data.contract";
import { jsonPatch, workspaceReducer } from "@open-smc/data/src/workspaceReducer";
import { Binding, isBinding } from "@open-smc/layout/src/contract/Binding";
import { pickBy } from "lodash-es";
import { selectDeep } from "@open-smc/data/src/selectDeep";
import { effect } from "@open-smc/utils/src/operators/effect";
import { selectByPath } from "@open-smc/data/src/selectByPath";
import { JSONPath } from "jsonpath-plus";
import { extractReferences } from "@open-smc/data/src/extractReferences";
import { serializeMiddleware } from "@open-smc/data/src/serializeMiddleware";

export const reverseDataBinding = (
    app$: Observable<AppState>,
    data$: Observable<unknown>,
    dataDispatch: Dispatch
) =>
    (layoutArea: LayoutArea) => {
        if (layoutArea) {
            const {control} = layoutArea;

            if (control) {
                const {dataContext, ...props} = control;

                if (dataContext) {
                    const bindings = pickBy(props, isBinding);

                    return data$
                        .pipe(map(selectDeep(dataContext)))
                        .pipe(distinctUntilChanged(isEqual))
                        .pipe(
                            effect(
                                dataContextState => {
                                    const dataContextWorkspace =
                                        configureStore({
                                            reducer: workspaceReducer,
                                            preloadedState: dataContextState,
                                            devTools: {
                                                name: layoutArea.id
                                            },
                                            middleware: getDefaultMiddleware =>
                                                getDefaultMiddleware()
                                                    .prepend(serializeMiddleware),
                                        });

                                    const subscription = new Subscription();

                                    subscription.add(
                                        from(dataContextWorkspace)
                                            .pipe(dataPatch(dataContext))
                                            .subscribe(dataDispatch)
                                    );

                                    subscription.add(
                                        app$
                                            .pipe(dataContextPatch(layoutArea.id, bindings))
                                            .subscribe(dataContextWorkspace.dispatch)
                                    );

                                    return subscription;
                                }
                            )
                        )
                        .subscribe();
                }
            }
        }
    }

const dataContextPatch = (layoutAreaId: string, bindings: Record<string, Binding>) =>
    (source: Observable<AppState>) =>
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
