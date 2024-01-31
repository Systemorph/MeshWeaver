import { useNotebookEditorSelector, useNotebookEditorStore } from "../../NotebookEditor";
// import { useNotebookEditorApi } from "./useNotebookEditorApi";
// import { without } from "lodash";
//
// export function useCutElements() {
//     const notebookEditorApi = useNotebookEditorApi();
//     const {setState} = useNotebookEditorStore();
//     const selectedElementIds = useNotebookEditorSelector("selectedElementIds");
//
//     return async () => {
//         try {
//             await notebookEditorApi.deleteElement(selectedElementIds);
//
//             setState(state => {
//                 state.elementIds = without(state.elementIds, ...selectedElementIds);
//
//                 state.clipboard = {
//                     elementIds: selectedElementIds,
//                     operation: 'cut'
//                 }
//
//                 state.selectedElementIds = [];
//                 state.activeElementId = null;
//             });
//         }
//         catch (error) {
//             // TODO: handle rejection(1/31/2023, akravets)
//         }
//     }
// }