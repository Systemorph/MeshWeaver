// import { distinctUntilChanged, from, map, merge, skip, Subscription } from "rxjs";
// import { app$ } from "./appStore";
// import { configureStore } from "@reduxjs/toolkit";
// import { isEqual } from "lodash";
// import { jsonPatchReducer } from "@open-smc/data/src/jsonPatchReducer";
// import { isBinding } from "@open-smc/layout/src/contract/Binding";
// import { pickBy, toPairs } from "lodash-es";
// import { selectDeep } from "@open-smc/data/src/operators/selectDeep";
// import { effect } from "@open-smc/utils/src/operators/effect";
// import { selectByPath } from "@open-smc/data/src/operators/selectByPath";
// import { extractReferences } from "@open-smc/data/src/operators/extractReferences";
// import { serializeMiddleware } from "@open-smc/data/src/middleware/serializeMiddleware";
// import { referenceToPatchAction } from "@open-smc/data/src/operators/referenceToPatchAction";
// import { bindingToPatchAction } from "./bindingToPatchAction";
// import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
// import { collections } from "./entityStore";
//
// export const reverseDataBinding = (
//     areaId: string
// ) =>
//     (control: UiControl) => {
//         if (control) {
//             const {dataContext, ...props} = control;
//
//             if (dataContext) {
//                 const bindings = pickBy(props, isBinding);
//
//                 const dataContextPatch$ =
//                     merge(
//                         ...toPairs(bindings)
//                             .map(
//                                 ([key, binding]) =>
//                                     app$
//                                         .pipe(map(appState => appState.areas[areaId].control.props[key]))
//                                         .pipe(distinctUntilChanged(isEqual))
//                                         .pipe(skip(1))
//                                         .pipe(map(bindingToPatchAction(binding)))
//                             )
//                     );
//
//                 return collections
//                     .pipe(map(selectDeep(dataContext)))
//                     .pipe(distinctUntilChanged(isEqual))
//                     .pipe(
//                         effect(
//                             dataContextState => {
//                                 const dataContextWorkspace =
//                                     configureStore({
//                                         reducer: jsonPatchReducer,
//                                         preloadedState: dataContextState,
//                                         devTools: {
//                                             name: areaId
//                                         },
//                                         middleware: getDefaultMiddleware =>
//                                             getDefaultMiddleware()
//                                                 .prepend(serializeMiddleware),
//                                     });
//
//                                 const dataContext$ = from(dataContextWorkspace);
//
//                                 const dataPatch$ =
//                                     merge(
//                                         ...extractReferences(dataContext)
//                                             .map(
//                                                 ([path, reference]) =>
//                                                     dataContext$
//                                                         .pipe(map(selectByPath(path)))
//                                                         .pipe(distinctUntilChanged(isEqual))
//                                                         .pipe(skip(1))
//                                                         .pipe(map(referenceToPatchAction(reference)))
//                                             )
//                                     );
//
//
//                                 const subscription = new Subscription();
//
//                                 subscription.add(
//                                     dataContextPatch$
//                                         .subscribe(dataContextWorkspace.dispatch)
//                                 );
//
//                                 subscription.add(
//                                     dataPatch$
//                                         .subscribe(console.log)
//                                 );
//
//                                 return subscription;
//                             }
//                         )
//                     )
//                     .subscribe();
//             }
//         }
//     }