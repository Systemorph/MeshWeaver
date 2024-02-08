import { MarkdownElement } from "../features/notebook/MarkdownElement";
import { CodeElement } from "../features/notebook/CodeElement";
import { useEffect, useMemo, useRef } from "react";
import { ElementToolbar } from "../features/notebook/ElementToolbar";
import classNames from "classnames";
import { useElementEvents } from "../features/notebook/useElementEvents";
import { ElementRunButton } from "../features/notebook/ElementRunButton";
import styles from "../features/notebook/element.module.scss";
import { isElementInViewport } from "../shared/utils/helpers";
import { formatNumber } from "@open-smc/utils/src/numbers";
import { useSubscribeToElementStatusChanged } from "../features/notebook/documentStore/hooks/useSubscribeToElementStatusChanged";
import { AreaChangedEvent } from "@open-smc/application/src/application.contract";
import { ElementKind, EvaluationStatus } from "../app/notebookFormat";
import {
    useElementsStore,
    useNotebookEditorSelector,
    useNotebookEditorStore
} from "../features/notebook/NotebookEditor";
import { useElement } from "../features/notebook/documentStore/hooks/useElement";
import { useSubscribeToElementContentChanged } from "../features/notebook/documentStore/hooks/useSubscribeToElementContentChanged";
import { ControlView } from "@open-smc/application/src/ControlDef";
import { useUpdateMarkdown } from "../features/notebook/documentStore/hooks/useUpdateMarkdown";
import { debounce } from "lodash";

export interface ElementEditorView extends ControlView {
    element: NotebookElementDto;
    output: AreaChangedEvent;
}

export interface NotebookElementDto {
    readonly id: string;
    readonly elementKind: ElementKind;
    readonly language?: string;
    readonly content: string;
    readonly metadata?: any;
    readonly version?: number;
    readonly evaluationStatus?: EvaluationStatus;
    readonly evaluationCount?: number;
    readonly evaluationTime?: number;
    readonly evaluationError?: string;
}

export default function ElementEditorControl({element, output}: ElementEditorView) {
    const {id: elementId, elementKind} = element;
    const model = useElement(elementId);
    const {setState} = useElementsStore();
    const markdown = useNotebookEditorSelector("markdown");
    const updateMarkdownDebounced = useNotebookEditorSelector("updateMarkdownDebouncedFunc");
    const updateMarkdown = useUpdateMarkdown();

    useEffect(() => {
        setState(elements => {
            elements[elementId].element = element;
            elements[elementId].output = output;

            if (elementKind === "markdown" && !markdown?.[elementId]) {
                updateMarkdownDebounced(updateMarkdown);
            }
        });
    }, [element, output, updateMarkdown]);

    if (!model.element) {
        return null;
    }

    return (
        <ElementEditor elementId={elementId}/>
    );
}

interface ElementEditorProps {
    elementId: string;
}

export function ElementEditor({elementId}: ElementEditorProps) {
    const activeElementId = useNotebookEditorSelector("activeElementId");
    const selectedElementIds = useNotebookEditorSelector("selectedElementIds");
    const codeCollapsed = useNotebookEditorSelector("codeCollapsed");
    const permissions = useNotebookEditorSelector("permissions");
    const {canEdit} = permissions;
    const model = useElement(elementId);

    // TODO: introduce store expressions trigger re-rendering only if the result is changed (8/10/2023, akravets)
    const enableKeyboardEvents = activeElementId === elementId;
    const isActive = activeElementId === elementId;
    const isSelected = selectedElementIds.includes(elementId);

    const {
        ref: elementRef,
        isEditMode,
        isAdded,
        element,
        output
    } = model;
    const {elementKind, evaluationCount, evaluationTime, evaluationError } = element;
    const contentRef = useRef<HTMLDivElement>();

    useElementEvents(elementId, elementKind, elementRef, enableKeyboardEvents, permissions);

    useEffect(() => {
        if (isAdded && !isElementInViewport(elementRef.current)) {
            elementRef.current.scrollIntoView();
        }
    }, []);

    useSubscribeToElementStatusChanged();
    useSubscribeToElementContentChanged();

    const renderedContent = elementKind === 'markdown'
        ? <MarkdownElement model={model} contentRef={contentRef} canEdit={canEdit}/>
        : <CodeElement isFocused={isActive} model={model} output={output} isCollapsed={codeCollapsed} canEdit={canEdit}/>;

    const className = classNames('elementWrapper', elementKind, {
        editMode: elementKind === 'markdown' && isEditMode,
        error: elementKind === 'code' && !!evaluationError,
        active: isActive,
        selected: isSelected,
        collapsed: codeCollapsed
    });

    return (
        <div ref={elementRef}
             className={className}
             data-cell-id={elementId}
             data-qa-cell-id={elementId}
             data-qa-cell-type={elementKind}
             data-qa-is-active={isActive}
        >
            <ElementRunButton model={model} disabled={!permissions.canRun}/>
            <div className={styles.toolbarWrapper}>
                {elementKind === 'code' && !evaluationError && evaluationCount > 0 &&
                    <div className={styles.evaluationBox} data-qa-stats>
                        <span className={styles.evaluationStatus} data-qa-evaluation-status><i className="sm sm-check"/></span>
                        <span className={styles.evaluationTime}
                              data-qa-evaluation-time>{formatNumber(evaluationTime / 1000, '0.#')}s</span>
                    </div>
                }
                {canEdit && <ElementToolbar model={model}/>}
            </div>
            <div ref={contentRef} className={`${styles.cellContent}`} data-qa-content>
                {renderedContent}
            </div>
        </div>
    );
}