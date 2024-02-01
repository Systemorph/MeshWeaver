import { SHOW_OUTLINE } from "../documentState";
import { useNotebookEditorSelector, useNotebookEditorStore } from "../../NotebookEditor";
import { triggerContentWidthChanged } from "../../contentWidthEvent";
import { SessionStorageWrapper } from "../../../../shared/utils/sessionStorageWrapper";

export function useShowOutline() {
    const {setState} = useNotebookEditorStore();
    const showOutline = useNotebookEditorSelector('showOutline');

    const toggleOutline = () => {
        setState(state => {
            state.showOutline = !state.showOutline;
        });
        triggerContentWidthChanged();
        SessionStorageWrapper.setItem(SHOW_OUTLINE, !showOutline);
    }

    return {showOutline, toggleOutline};
}