import { useState, useCallback, useEffect, MutableRefObject, Ref } from "react";
import { IKeyboardEvent, KeyCode } from "monaco-editor";
import * as monaco from "monaco-editor";
import { ElementEditor, ElementEditorApi } from "./ElementEditor";
import { Button } from "@open-smc/ui-kit/src/components/Button";
import {MarkdownExtension} from "../../shared/monaco-markdown/markdownExtension";
import { ElementFooter } from "./ElementFooter";
import styles from "./element.module.scss";
import { useSelectNextElement } from "./documentStore/hooks/useSelectNextElement";
import { useEventListener } from "usehooks-ts";
import { Html } from "@open-smc/ui-kit/src/components/Html";
import mdStyles from '../../shared/components/markdown.module.scss';
import classNames from "classnames";
import button from "@open-smc/ui-kit/src/components/buttons.module.scss"
import { useElementsStore, useNotebookEditorSelector } from "./NotebookEditor";
import { NotebookElementModel } from "./documentStore/documentState";

interface MarkdownElementProps {
    model: NotebookElementModel;
    contentRef: MutableRefObject<HTMLDivElement>;
    canEdit: boolean;
}

export function MarkdownElement({model, contentRef, canEdit}: MarkdownElementProps) {
    const {element} = model;
    const elementsStore = useElementsStore();

    // TODO: move up and use one listener per notebook (11/21/2022, akravets)
    useEventListener('dblclick', () => {
        if (canEdit) {
            elementsStore.setState(models => {
                models[element.id].isEditMode = true;
            })
        }
    }, contentRef);

    return model.isEditMode
        ? <MarkdownEditor elementId={element.id} content={element.content} editorRef={model.editorRef}/>
        : <MarkdownViewer elementId={element.id}/>;
}

interface EditorProps {
    elementId: string;
    content: string;
    editorRef: Ref<ElementEditorApi>
}

const editorOptions: monaco.editor.IStandaloneEditorConstructionOptions = {
    wordWrap: 'on',
    lineNumbers: 'off',
    glyphMargin: false,
    lineDecorationsWidth: 0,
};

function MarkdownEditor({elementId, content, editorRef}: EditorProps) {
    const [editor, setEditor] = useState<monaco.editor.IStandaloneCodeEditor>();
    const elementsStore = useElementsStore();
    const selectNextElement = useSelectNextElement();

    const onKeydown = useCallback((event: IKeyboardEvent) => {
        if (event.keyCode === KeyCode.Escape || (event.keyCode === KeyCode.Enter && (event.shiftKey || event.altKey || event.ctrlKey))) {
            (event.shiftKey || event.altKey) && selectNextElement(elementId, true, 'FirstLine', true);
            // TODO: workaround for monaco markdown plugin, rewrite when get rid of that plugin (avinokurov, 6/6/2022)
            event.preventDefault();
            event.stopPropagation();
            elementsStore.setState(models => {
                models[elementId].isEditMode = false;
            })
        }
    }, []);

    const toolbar = editor ? <EditorToolbar editor={editor}/> : null;

    return (
        <div>
            <div className={styles.markdownInput}>
                <ElementEditor
                    language='markdown'
                    content={content}
                    elementId={elementId}
                    ref={editorRef}
                    focus={true}
                    onKeydown={onKeydown}
                    options={editorOptions}
                    onEditorReady={editor => setEditor(editor)}
                />
            </div>
            <ElementFooter language='Md' children={toolbar}/>
        </div>
    );
}

interface EditorToolbarProps {
    editor: monaco.editor.IStandaloneCodeEditor;
}

function EditorToolbar({editor}: EditorToolbarProps) {
    useEffect(() => {
        const monacoMarkdown = new MarkdownExtension();
        monacoMarkdown.activate(editor);
    }, [editor]);

    const triggerAction = useCallback((action: string) => {
        editor.trigger(null, `markdown.extension.editing.${action}`, null);
        editor.focus();
    }, [editor]);

    return (
        <div className={styles.markdownToolbar}>
            <Button type="button"
                    icon="sm sm-bold"
                    className={classNames(styles.markdownButton, button.button)}
                    onClick={() => triggerAction('toggleBold')}
                    data-qa-btn-bold/>

            <Button type="button"
                    icon="sm sm-italic"
                    className={classNames(styles.markdownButton, button.button)}
                    onClick={() => triggerAction('toggleItalic')}
                    data-qa-btn-italic/>

            <Button type="button"
                    icon="sm sm-headers"
                    className={classNames(styles.markdownButton, button.button)}
                    onClick={() => triggerAction('toggleHeadingUp')}
                    data-qa-btn-header/>

            <Button type="button"
                    icon="sm sm-strike-through"
                    className={classNames(styles.markdownButton, button.button)}
                    onClick={() => triggerAction('toggleStrikethrough')}
                    data-qa-btn-strike/>

            <Button type="button"
                    icon="sm sm-code"
                    className={classNames(styles.markdownButton, button.button)}
                    onClick={() => triggerAction('toggleCodeSpan')}
                    data-qa-btn-code/>
        </div>
    );
}

interface ViewerProps {
    elementId: string;
}

const emptyText = 'Empty markdown cell, double click or enter to edit';

function MarkdownViewer({elementId}: ViewerProps) {
    const markdown = useNotebookEditorSelector("markdown");

    // TODO: html is expected to be trimmed and to contain no empty tags after parsing (2/18/2022, akravets)
    return <Html html={markdown?.[elementId].html || emptyText} className={mdStyles.markdownContent}/>;
}
