import {
    CODE_COLLAPSED,
    NotebookEditorState,
    NotebookElementModel,
    SHOW_OUTLINE
} from "./documentStore/documentState";
import { createStore, Store } from "@open-smc/store/store";
import { createContext, createRef, useContext, useEffect, useState } from "react";
import loader from "@open-smc/ui-kit/components/loader.module.scss";
import { getDocumentMarkdown } from "./documentStore/markdownTools";
import { debounce, head, keyBy } from "lodash";
import { NotebookDto } from "../../controls/NotebookEditorControl";
import { SessionStorageWrapper } from "../../shared/utils/sessionStorageWrapper";
import {
    useSubscribeToSessionEvaluationStatusChanged
} from "./documentStore/hooks/useSubscribeToSessionEvaluationStatusChanged";
import { useSubscribeToSessionDialogEvent } from "./useSubscribeToSessionDialogEvent";
import { useSubscribeToSessionStatusEvent } from "./documentStore/hooks/useSubscribeToSessionStatusEvent";
import { useSubscribeToDeletedElements } from "./documentStore/hooks/useSubscribeToDeletedElements";
import { useSubscribeToMovedElements } from "./documentStore/hooks/useSubscribeToMovedElements";
import { useShowOutline } from "./documentStore/hooks/useShowOutline";
import styles from "./notebook.module.scss";
import { Header } from "./Header";
import classNames from "classnames";
import { TableOfContents } from "./TableOfContents";
import { Elements } from "./Elements";
import { NotebookSessionDialog } from "./NotebookSessionDialog";
import { useSubscribeToNewElements } from "./documentStore/hooks/useSubscribeToNewElements";
import { NotebookElementDto } from "../../controls/ElementEditorControl";
import { useProjectSelector } from "../project/projectStore/projectStore";
import { useApi } from "../../ApiProvider";
import { getUseSelector } from "@open-smc/store/useSelector";

interface NotebookEditorContext {
    readonly store: Store<NotebookEditorState>;
    readonly elementsStore: Store<Record<string, NotebookElementModel>>;
}

const notebookContext = createContext<NotebookEditorContext>(null);

export function useNotebookEditorContext() {
    return useContext(notebookContext);
}

export function useNotebookEditorStore() {
    const {store} = useNotebookEditorContext();
    return store;
}

export const useNotebookEditorSelector = getUseSelector(useNotebookEditorStore);

export function useElementsStore() {
    const {elementsStore} = useNotebookEditorContext();
    return elementsStore;
}


interface NotebookEditorProps {
    notebook: NotebookDto;
    projectId: string;
    envId: string;
    canEdit: boolean;
    canRun: boolean;
}

export function NotebookEditor({notebook, projectId, envId, canEdit, canRun}: NotebookEditorProps) {
    const {path: notebookPath} = useProjectSelector("activeFile");
    const [contextValue, setContextValue] = useState<NotebookEditorContext>();
    const {EnvAccessControlApi} = useApi();

    useEffect(() => {
        (async function () {
            const {elementIds} = notebook;
            const elementModels = elementIds.map(elementId => getElementModel(elementId));
            const elementsById = keyBy(elementModels, c => c.elementId);
            // const markdown = getDocumentMarkdown(elementIds, elementsById, projectId, envId, notebookPath);
            const {currentSession, evaluationStatus, sessionDialog} = notebook;
            const activeElementId = head(elementIds);
            const selectedElementIds = activeElementId ? [activeElementId] : [];
            const showOutline = SessionStorageWrapper.getItem(SHOW_OUTLINE);
            const codeCollapsed = SessionStorageWrapper.getItem(CODE_COLLAPSED);

            const initialState = {
                notebook,
                projectId,
                envId,
                elementIds,
                selectedElementIds,
                activeElementId,
                updateMarkdownDebouncedFunc: debounce(func => func(), 100),
                session: currentSession ?? {status: 'Stopped'},
                sessionStatus: currentSession?.status ?? 'Stopped',
                evaluationStatus,
                showOutline,
                codeCollapsed,
                sessionDialog,
                elementChanges: {
                    buffer: [],
                    debouncedFunc: debounce(func => func(), 100)
                },
                elementEvents: true,
                permissions: {
                    canEdit,
                    canRun
                }
            } as NotebookEditorState;

            setContextValue({
                store: createStore(initialState),
                elementsStore: createStore(elementsById)
            });
        })();
    }, [notebook.id]); //[projectId, envId, notebook.id, notebookPath]);

    if (!contextValue) {
        return <div className={loader.loading}>Loading...</div>;
    }

    return (
        <notebookContext.Provider value={contextValue}>
            <NotebookEditorInner/>
        </notebookContext.Provider>
    );
}

export function getElementModel(elementId: string, element: NotebookElementDto = null, isAdded = false, isEditMode = false) {
    return {
        elementId,
        ref: createRef(),
        editorRef: createRef(),
        element,
        isAdded,
        isEditMode
    } as NotebookElementModel;
}

function NotebookEditorInner() {
    const {setState} = useNotebookEditorStore();

    useSubscribeToSessionEvaluationStatusChanged();
    // useSubscribeToSessionDialogEvent(sessionDialogPresenter => {
    //     setState(state => {
    //         state.sessionDialog = sessionDialogPresenter
    //     });
    // });
    useSubscribeToSessionStatusEvent();
    useSubscribeToNewElements();
    useSubscribeToDeletedElements();
    useSubscribeToMovedElements();

    const {showOutline} = useShowOutline();

    return (
        <div className={styles.notebook}>
            <Header/>
            <div id='scrollable-container' className={classNames(styles.mainWrapper, {showOutline})}>
                {
                    showOutline &&
                    <div className={styles.outlineWrapper}>
                        <TableOfContents/>
                    </div>
                }
                <div className={classNames(styles.main, {showOutline})} data-qa-elements>
                    <Elements/>
                </div>
            </div>
            {/*<NotebookSessionDialog/>*/}
        </div>
    );
}