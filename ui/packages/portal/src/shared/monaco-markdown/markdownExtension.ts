import {editor, KeyCode, KeyMod} from 'monaco-editor';
import {TextEditor} from "./TextEditor";
import {Position, Range, WorkspaceEdit, Selection} from "./extHostTypes";
export class MarkdownExtension {
    activate(editor: editor.IStandaloneCodeEditor) {
        let textEditor = new TextEditor(editor)

        activateFormatting(textEditor)
    }
}

function addKeybinding(editor: TextEditor, name: String, fun: CallableFunction, keybindings: number[], label?: string, context?: string, contextMenuGroupId = "markdown.extension.editing") {
    editor.addAction({
        contextMenuGroupId: contextMenuGroupId,
        contextMenuOrder: 0,
        id: "markdown.extension.editing." + name,
        keybindingContext: context,
        keybindings: keybindings,
        label: label,
        precondition: "",
        run(_: editor.ICodeEditor): void | Promise<void> {
            fun(editor)
            return undefined;
        }
    });
}

function activateFormatting(editor: TextEditor) {
    addKeybinding(editor, "toggleBold", toggleBold, [KeyMod.CtrlCmd | KeyCode.KeyB], "Toggle bold");
    addKeybinding(editor, "toggleItalic", toggleItalic, [KeyMod.CtrlCmd | KeyCode.KeyI], "Toggle italic");
    addKeybinding(editor, "toggleCodeSpan", toggleCodeSpan, [KeyMod.CtrlCmd | KeyCode.Backquote], "Toggle code span");
    addKeybinding(editor, "toggleStrikethrough", toggleStrikethrough, [KeyMod.Alt | KeyCode.KeyS], "Toggle strikethrough");
    addKeybinding(editor, "toggleHeadingUp", toggleHeadingUp, [KeyMod.WinCtrl | KeyMod.Shift | KeyCode.BracketLeft], "Heading up");
}

function toggleBold(editor: TextEditor) {
    return styleByWrapping(editor, '**');
}

function toggleItalic(editor: TextEditor) {
    // let indicator = workspace.getConfiguration('markdown.extension.italic').get<string>('indicator');
    return styleByWrapping(editor, '*');
}

function toggleCodeSpan(editor: TextEditor) {
    return styleByWrapping(editor, '`');
}

function toggleStrikethrough(editor: TextEditor) {
    return styleByWrapping(editor, '~~');
}

const maxHeading = '######';

function toggleHeadingUp(editor: TextEditor) {
    let lineIndex = editor.selection.active.line;
    let lineText = editor.document.lineAt(lineIndex).text;

    return editor.edit((editBuilder) => {
        if (!lineText.startsWith('#')) { // Not a heading
            editBuilder.insert(new Position(lineIndex, 0), '# ');
        } else if (lineText.startsWith(maxHeading)) { // Reset heading at 6 level
            let deleteIndex = lineText.startsWith(maxHeading + ' ') ? maxHeading.length + 1 : maxHeading.length;
            editBuilder.delete(new Range(new Position(lineIndex, 0), new Position(lineIndex, deleteIndex)));
        } else {
            editBuilder.insert(new Position(lineIndex, 0), '#');
        }
    });
}

function getContext(editor: TextEditor, cursorPos: Position, startPattern: string, endPattern?: string): string {
    if (endPattern == undefined) {
        endPattern = startPattern;
    }

    let startPositionCharacter = cursorPos.character - startPattern.length;
    let endPositionCharacter = cursorPos.character + endPattern.length;

    if (startPositionCharacter < 0) {
        startPositionCharacter = 0;
    }

    let leftText = editor.document.getText(new Range(cursorPos.line, startPositionCharacter, cursorPos.line, cursorPos.character));
    let rightText = editor.document.getText(new Range(cursorPos.line, cursorPos.character, cursorPos.line, endPositionCharacter));

    if (rightText == endPattern) {
        if (leftText == startPattern) {
            return `${startPattern}|${endPattern}`;
        } else {
            return `${startPattern}text|${endPattern}`;
        }
    }
    return '|';
}

function isWrapped(text: string, startPattern: string, endPattern?: string): boolean {
    if (endPattern == undefined) {
        endPattern = startPattern;
    }
    return text.startsWith(startPattern) && text.endsWith(endPattern);
}

function wrapRange(editor: TextEditor, wsEdit: WorkspaceEdit, shifts: [Position, number][], newSelections: Selection[], i: number, shift: number, cursor: Position, range: Range, isSelected: boolean, startPtn: string, endPtn?: string) {
    if (endPtn == undefined) {
        endPtn = startPtn;
    }

    let text = editor.document.getText(range);
    const prevSelection = newSelections[i];
    const ptnLength = (startPtn + endPtn).length;

    let newCursorPos = cursor.with({character: cursor.character + shift});
    let newSelection: Selection;
    if (isWrapped(text, startPtn)) {
        // remove start/end patterns from range
        wsEdit.replace(editor.document.uri, range, text.substr(startPtn.length, text.length - ptnLength));

        shifts.push([range.end, -ptnLength]);

        // Fix cursor position
        if (!isSelected) {
            if (!range.isEmpty) { // means quick styling
                if (cursor.character == range.end.character) {
                    newCursorPos = cursor.with({character: cursor.character + shift - ptnLength});
                } else {
                    newCursorPos = cursor.with({character: cursor.character + shift - startPtn.length});
                }
            } else { // means `**|**` -> `|`
                newCursorPos = cursor.with({character: cursor.character + shift + startPtn.length});
            }
            newSelection = new Selection(newCursorPos, newCursorPos);
        } else {
            newSelection = new Selection(
                prevSelection.start.with({character: prevSelection.start.character + shift}),
                prevSelection.end.with({character: prevSelection.end.character + shift - ptnLength})
            );
        }
    } else {
        // add start/end patterns around range
        wsEdit.replace(editor.document.uri, range, startPtn + text + endPtn);

        shifts.push([range.end, ptnLength]);

        // Fix cursor position
        if (!isSelected) {
            if (!range.isEmpty) { // means quick styling
                if (cursor.character == range.end.character) {
                    newCursorPos = cursor.with({character: cursor.character + shift + ptnLength});
                } else {
                    newCursorPos = cursor.with({character: cursor.character + shift + startPtn.length});
                }
            } else { // means `|` -> `**|**`
                newCursorPos = cursor.with({character: cursor.character + shift + startPtn.length});
            }
            newSelection = new Selection(newCursorPos, newCursorPos);
        } else {
            newSelection = new Selection(
                prevSelection.start.with({character: prevSelection.start.character + shift}),
                prevSelection.end.with({character: prevSelection.end.character + shift + ptnLength})
            );
        }
    }

    newSelections[i] = newSelection;
}

function styleByWrapping(editor: TextEditor, startPattern: string, endPattern?: string) {
    if (endPattern == undefined) {
        endPattern = startPattern;
    }

    let selections = editor.selections;

    let batchEdit = new WorkspaceEdit();
    let shifts: [Position, number][] = [];
    let newSelections: Selection[] = selections.slice();

    selections.forEach((selection, i) => {
        let cursorPos = selection.active;
        const shift = shifts.map(([pos, s]) => (selection.start.line == pos.line && selection.start.character >= pos.character) ? s : 0)
            .reduce((a, b) => a + b, 0);

        if (selection.isEmpty) {
            // No selected text
            if (startPattern !== '~~' && getContext(editor, cursorPos, startPattern) === `${startPattern}text|${endPattern}`) {
                // `**text|**` to `**text**|`
                let newCursorPos = cursorPos.with({character: cursorPos.character + shift + endPattern.length});
                newSelections[i] = new Selection(newCursorPos, newCursorPos);
                return;
            } else if (getContext(editor, cursorPos, startPattern) === `${startPattern}|${endPattern}`) {
                // `**|**` to `|`
                let start = cursorPos.with({character: cursorPos.character - startPattern.length});
                let end = cursorPos.with({character: cursorPos.character + endPattern.length});
                wrapRange(editor, batchEdit, shifts, newSelections, i, shift, cursorPos, new Range(start, end), false, startPattern);
            } else {
                // Select word under cursor
                let wordRange = editor.document.getWordRangeAtPosition(cursorPos);
                if (wordRange == undefined) {
                    wordRange = selection;
                }
                // One special case: toggle strikethrough in task list
                const currentTextLine = editor.document.lineAt(cursorPos.line);
                if (startPattern === '~~' && /^\s*[\*\+\-] (\[[ x]\] )? */g.test(currentTextLine.text)) {
                    wordRange = currentTextLine.range.with(new Position(cursorPos.line, currentTextLine.text.match(/^\s*[\*\+\-] (\[[ x]\] )? */g)[0].length));
                }
                wrapRange(editor, batchEdit, shifts, newSelections, i, shift, cursorPos, wordRange, false, startPattern);
            }
        } else {
            // Text selected
            wrapRange(editor, batchEdit, shifts, newSelections, i, shift, cursorPos, selection, true, startPattern);
        }
    });

    const hasSelection = editor.selection && !editor.selection.isEmpty;

    return editor.applyEdit(batchEdit, newSelections).then(() => {
        if (!hasSelection) {
            editor.selections = newSelections;
        }
    });
}