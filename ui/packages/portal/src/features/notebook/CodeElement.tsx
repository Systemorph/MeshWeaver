import { NotebookElementModel } from "./documentStore/documentState";
import { editor, IKeyboardEvent, KeyCode } from "monaco-editor";
import { ElementEditor } from "./ElementEditor";
import { useCallback } from "react";
import { useEvaluateElements } from "./documentStore/hooks/useEvaluateElements";
import { useSelectNextElement } from "./documentStore/hooks/useSelectNextElement";
import styles from "./element.module.scss";
import { AreaChangedEvent } from "@open-smc/application/src/application.contract";
import { Output } from "./Output";

interface Props {
    model: NotebookElementModel;
    output: AreaChangedEvent;
    isFocused: boolean;
    isCollapsed: boolean;
    canEdit: boolean;
}

export function CodeElement({model, output, isFocused, isCollapsed, canEdit}: Props) {
    const {element, editorRef} = model;
    const {evaluationError, id, content, language} = element;
    const evaluateElements = useEvaluateElements();
    const selectNextElement = useSelectNextElement();

    const onEditorKeydown = useCallback(async (event: IKeyboardEvent) => {
        if (event.keyCode === KeyCode.Enter && (event.shiftKey || event.altKey || event.ctrlKey)) {
            evaluateElements([id]);
            (event.altKey || event.shiftKey) && selectNextElement(id, true, 'FirstLine', true);
            event.preventDefault();
            event.stopPropagation();
        }
    }, []);

    const editorOptions: editor.IStandaloneEditorConstructionOptions = {
        readOnly: !canEdit
    };

    return (
        <div>
            <div className={styles.inputWrapper}>
                {isCollapsed
                    ? <i className={styles.collapsedTitle}>Collapsed code</i>
                    : (
                        <div className={styles.input}>
                            <div className={styles.code}>
                                <ElementEditor
                                    language={language ?? "csharp"}
                                    content={content}
                                    onKeydown={onEditorKeydown}
                                    elementId={id}
                                    ref={editorRef}
                                    focus={isFocused}
                                    options={editorOptions}
                                />
                            </div>
                        </div>
                    )
                }
                {!!evaluationError &&
                    <div className={styles.evaluationBox} data-qa-evaluation-error>
                        <i className="sm sm-alert"/>
                        <div className={styles.evaluationError}>
                            {evaluationError}
                        </div>
                    </div>
                }
            </div>
            {output && <Output event={output}/>}
        </div>
    );
}