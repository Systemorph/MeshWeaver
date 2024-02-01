import * as monaco from "monaco-editor";
import { forwardRef, useEffect, useImperativeHandle, useRef, useState } from "react";
import {IKeyboardEvent} from "monaco-editor";
import {useChangeElementContent} from "./documentStore/hooks/useChangeElementContent";
import {subscribeToContentWidthChanged} from "./contentWidthEvent";
import {useSelectNextElement} from "./documentStore/hooks/useSelectNextElement";
import {useSelectPreviousElement} from "./documentStore/hooks/useSelectPreviousElement";
import style from "../../variables.module.scss";
import { NotebookElementChangeData } from "./notebookElement.contract";

interface Props {
    content: string;
    elementId: string;
    focus?: boolean;
    language: string;
    onKeydown?: (e: IKeyboardEvent) => any;
    options?: monaco.editor.IStandaloneEditorConstructionOptions;
    onEditorReady?: (editor: monaco.editor.IStandaloneCodeEditor) => void;
}

export type FocusMode = 'FirstLine' | 'LastLine';

export interface ElementEditorApi {
    focus: (mode: FocusMode) => void;
    applyEdits: (edits: NotebookElementChangeData[]) => void;
}

export const ElementEditor = forwardRef<ElementEditorApi, Props>((props, ref) => {
    const {content, elementId, focus, language, onKeydown, options, onEditorReady} = props;
    const {changeElementContent, flushElementContentChanges} = useChangeElementContent();
    const containerRef = useRef<HTMLDivElement>(null);
    const selectNextElement = useSelectNextElement();
    const selectPreviousElement = useSelectPreviousElement();
    const [editor, setEditor] = useState<monaco.editor.IStandaloneCodeEditor>();
    const suppressChangeEvents = useRef(false);

    useImperativeHandle(ref, () => {
        return {
            focus: (mode) => {
                editor.focus();
                switch (mode) {
                    case 'FirstLine':
                        editor.setPosition({lineNumber: 1, column: 1});
                        break;
                    case 'LastLine':
                        const lineCount = editor.getModel().getLineCount();
                        editor.setPosition({lineNumber: lineCount, column: 1});
                        break;            
                    default:
                        break;
                }
            },
            applyEdits: (edits: NotebookElementChangeData[]) => {
                suppressChangeEvents.current = true;
                editor.getModel().applyEdits(edits.map(convertNotebookElementChange));
                suppressChangeEvents.current = false;
            }
        }
    });

    useEffect(() => {
        const model = monaco.editor.createModel(content, language);
        const editor = monaco.editor.create(containerRef.current, {model, ...defaultOptions, ...options});
        const firstLineContextKey = 'firstLine';
        const lastLineContextKey = 'lastLine';

        const isFirstLine = () => {
            return editor.getPosition().lineNumber === 1;
        }

        const isLastLine = () => {
            return editor.getPosition().lineNumber === editor.getModel().getLineCount();
        }

        const setupKeyBindingContext = (editor: monaco.editor.IStandaloneCodeEditor) => {
            const firstLineContext = editor.createContextKey(firstLineContextKey, isFirstLine());
            const lastLineContext = editor.createContextKey(lastLineContextKey, isLastLine());

            editor.onDidChangeCursorPosition(() => {
                firstLineContext.set(isFirstLine());
                lastLineContext.set(isLastLine());
            });

        }

        const addKeyBindings = () => {
            setupKeyBindingContext(editor);

            editor.addAction({
                label: "Go to the previous cell",
                id: 'switchToPrevCell' + elementId,
                keybindingContext: `${firstLineContextKey} && !suggestWidgetVisible && !parameterHintsVisible`,
                keybindings: [monaco.KeyCode.UpArrow],
                run(_: monaco.editor.ICodeEditor): void | Promise<void> {
                    selectPreviousElement(elementId, true, 'LastLine');
                }
            });

            editor.addAction({
                label: "Go to the next cell",
                id: 'switchToNextCell' + elementId,
                keybindingContext: `${lastLineContextKey} && !suggestWidgetVisible && !parameterHintsVisible`,
                keybindings: [monaco.KeyCode.DownArrow],
                run(_: monaco.editor.ICodeEditor): void | Promise<void> {
                    selectNextElement(elementId, true, 'FirstLine');
                }
            });
        }
        addKeyBindings();

        model.setEOL(monaco.editor.EndOfLineSequence.LF);

        const updateHeight = () => {
            const height = editor.getContentHeight();
            const width = containerRef.current.clientWidth;
            containerRef.current.style.height = `${height}px`;
            editor.layout({width, height});
        };

        editor.onDidContentSizeChange(updateHeight);

        editor.onDidChangeModelContent(event => {
            if (!suppressChangeEvents.current) {
                changeElementContent(elementId, event.changes.map(convertModelContentChange), model.getValue())
            }
        });

        editor.onDidBlurEditorText(flushElementContentChanges);

        if (onKeydown) {
            editor.onKeyDown(onKeydown);
        }

        if (onEditorReady) {
            onEditorReady(editor);
        }

        const resetEditorLayout = () => {
            editor.layout({width: 0, height: 0});

            window.requestAnimationFrame(() => {
                const parent = containerRef.current?.parentElement;

                if (parent) {
                    const rect = parent.getBoundingClientRect()
                    editor.layout({width: rect.width, height: rect.height})
                }
            })
        }

        const unsubscribeFromContentWidthChanged = subscribeToContentWidthChanged(resetEditorLayout);
        setEditor(editor);

        return () => {
            unsubscribeFromContentWidthChanged();
            editor.dispose();
        };
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [elementId]);

    useEffect(() => {
        if (focus && editor) {
            editor.focus();
        }
    }, [editor]);

    return <div data-qa-input ref={containerRef}/>;
});

function convertModelContentChange({range, text}: monaco.editor.IModelContentChange): NotebookElementChangeData {
    return {
        startLineNumber: range.startLineNumber - 1,
        endLineNumber: range.endLineNumber - 1,
        startColumn: range.startColumn - 1,
        endColumn: range.endColumn - 1,
        text
    };
}

function convertNotebookElementChange(event: NotebookElementChangeData): monaco.editor.IIdentifiedSingleEditOperation {
    return {
        range: {
            startLineNumber: event.startLineNumber + 1,
            endLineNumber: event.endLineNumber + 1,
            startColumn: event.startColumn + 1,
            endColumn: event.endColumn + 1
        },
        text: event.text,
        forceMoveMarkers: true
    };
}

const defaultOptions: monaco.editor.IStandaloneEditorConstructionOptions = {
    minimap: {
        enabled: false
    },
    scrollBeyondLastLine: false,
    renderLineHighlight: 'none',
    fontFamily: 'monospace',
    fixedOverflowWidgets: true,
    fontSize: 14,
    lineHeight: 24,
    autoIndent: "full",
    glyphMargin: false,
    folding: false,


    // disableLayerHinting: true,
    // formatOnType: true,
    hideCursorInOverviewRuler: true,
    lineDecorationsWidth: 16,
    lineNumbersMinChars: 1,

    // overviewRulerBorder: false,
    scrollbar: {
        alwaysConsumeMouseWheel: false,
        // horizontal: "visible"
    },
    theme: "CellTheme",
    overviewRulerLanes: 0,
    unicodeHighlight: {
        invisibleCharacters: false,
        ambiguousCharacters: false
    }
};

monaco.editor.defineTheme('CellTheme', {
    base: 'vs',
    inherit: true,
    rules: [],
    colors: {
        'editor.foreground': style.gunmetal,
        'editorLineNumber.foreground': style.silverSand,
        'editorLineNumber.activeForeground': style.smBlue,
    }
});