import type {
    ElementEditorView,
    NotebookElementDto
} from "@open-smc/portal/src/controls/ElementEditorControl";
import { ControlDef } from "@open-smc/application/src/ControlDef";
import { AreaChangedEvent } from "@open-smc/application/src/application.contract";
import { NotebookEditor } from "./NotebookEditor";
import {NotebookElementContentChangedEvent} from "@open-smc/portal/src/features/notebook/notebookElement.contract";
import {debounce} from "lodash";
import {applyContentEdits} from "@open-smc/portal/src/features/notebook/documentStore/applyContentEdits";
import { ControlBase } from "./ControlBase";

export class ElementEditor extends ControlBase implements ElementEditorView {
    output = new AreaChangedEvent("output");

    constructor(public element: NotebookElementDto, private notebookEditor: NotebookEditor, private onChange?: () => void) {
        super("ElementEditorControl");

        this.receiveMessage(NotebookElementContentChangedEvent,
            this.getNotebookElementContentChangedHandler(),
            ({message}) => message.elementId === this.element.id);
    }

    setOutput(control: ControlDef) {
        this.setArea(this.output.area, control);
    }

    private getNotebookElementContentChangedHandler() {
        let buffer: NotebookElementContentChangedEvent[] = [];

        const applyChangesDebounced = debounce(() => {
            buffer.forEach(event => {
                const {elementId, changes} = event;

                if (elementId === this.element.id) {
                    let content = applyContentEdits(this.element.content, changes)
                    this.element = {...this.element, content: content};
                    this.notebookEditor.elementsById[elementId] = {...this.notebookEditor.elementsById[elementId], content: content};
                }
            });

            this.onChange?.();

            buffer = [];
        }, 0);

        return (event: NotebookElementContentChangedEvent) => {
            buffer.push(event);
            applyChangesDebounced();
            this.sendMessage({...event, status: "Committed"});
        }
    }
}