import { distinctUntilChanged, from, map, merge, Observable, skip, Subscription } from "rxjs";
import { AppState } from "./appStore";
import { configureStore, Dispatch } from "@reduxjs/toolkit";
import { LayoutArea } from "@open-smc/layout/src/contract/LayoutArea";
import { isEqual } from "lodash";
import { workspaceReducer } from "@open-smc/data/src/workspaceReducer";
import { isBinding } from "@open-smc/layout/src/contract/Binding";
import { pickBy, toPairs } from "lodash-es";
import { selectDeep } from "@open-smc/data/src/selectDeep";
import { effect } from "@open-smc/utils/src/operators/effect";
import { selectByPath } from "@open-smc/data/src/selectByPath";
import { extractReferences } from "@open-smc/data/src/extractReferences";
import { serializeMiddleware } from "@open-smc/data/src/serializeMiddleware";
import { referenceToPatchAction } from "./referenceToPatchAction";
import { bindingToPatchAction } from "./bindingToPatchAction";

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

                    const dataContextPatch$ =
                        merge(
                            ...toPairs(bindings)
                                .map(
                                    ([key, binding]) =>
                                        app$
                                            .pipe(map(appState => appState.areas[layoutArea.id].control.props[key]))
                                            .pipe(distinctUntilChanged(isEqual))
                                            .pipe(skip(1))
                                            .pipe(map(bindingToPatchAction(binding)))
                                )
                        );

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

                                    const dataContext$ = from(dataContextWorkspace);

                                    const dataPatch$ =
                                        merge(
                                            ...extractReferences(dataContext)
                                                .map(
                                                    ({path, reference}) =>
                                                        dataContext$
                                                            .pipe(map(selectByPath(path)))
                                                            .pipe(distinctUntilChanged(isEqual))
                                                            .pipe(skip(1))
                                                            .pipe(map(referenceToPatchAction(reference)))
                                                )
                                        );


                                    const subscription = new Subscription();

                                    subscription.add(
                                        dataContextPatch$
                                            .subscribe(dataContextWorkspace.dispatch)
                                    );

                                    subscription.add(
                                        dataPatch$
                                            .subscribe(dataDispatch)
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