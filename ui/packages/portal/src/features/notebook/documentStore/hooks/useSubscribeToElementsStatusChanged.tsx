import { useElementsStore, useNotebookEditorStore } from "../../NotebookEditor";
import { intersection } from "lodash";
// import { useNotebookEditorApi } from "./useNotebookEditorApi";
// import { useEffect } from "react";
//
// export function useSubscribeToElementsStatusChanged() {
//     const {getState} = useNotebookEditorStore();
//     const elementsStore = useElementsStore();
//     const notebookEditorApi = useNotebookEditorApi();
//
//     useEffect(() => {
//         return notebookEditorApi.subscribeToElementStatusBulkEvent(event => {
//             const {elementIds} = getState();
//             const {evaluationStatus} = event;
//             const elementIdsToChange = intersection(event.elementIds, elementIds);
//
//             elementsStore.setState(models => {
//                 for (const elementId of elementIdsToChange) {
//                     models[elementId].element.evaluationStatus = evaluationStatus;
//                 }
//             })
//         });
//     }, []);
// }