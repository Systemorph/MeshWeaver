import type { NotebookDto, NotebookEditorView } from "@open-smc/portal/src/controls/NotebookEditorControl";
import {
    EvaluateElementsCommand,
    NotebookElementCreatedEvent,
    NotebookElementDeletedEvent, NotebookElementEvaluationStatusEvent,
    NotebookElementMovedEvent,
    SessionEvaluationStatusChangedEvent,
    SessionStatusEvent, StartSessionEvent,
    StopSessionEvent
} from "@open-smc/portal/src/features/notebook/notebookEditor/notebookEditor.contract";
import { NotebookElementDto } from "@open-smc/portal/src/controls/ElementEditorControl";
import {keyBy, map, without} from "lodash";
import { ElementEditor } from "./ElementEditor";
import { v4 } from "uuid";
import { insertAfter } from "@open-smc/utils/src/insertAfter";
import { EvaluationStatus } from "@open-smc/portal/src/app/notebookFormat";
import {moveElements} from "@open-smc/portal/src/features/notebook/documentStore/moveElements";
import { makeHtml } from "./Html";
import { ControlBase } from "./ControlBase";

export class NotebookEditor extends ControlBase implements NotebookEditorView {
    readonly elementsById: Record<string, NotebookElementDto>;
    elementEditorsById: Record<string, ElementEditor> = {};
    notebook: NotebookDto;
    evaluationStatus: EvaluationStatus;

    executionCount = 1;

    constructor(elements: NotebookElementDto[], private onChange?: () => void) {
        super("NotebookEditorControl");

        const elementIds = map(elements, "id");
        this.elementsById = keyBy(elements, "id");

        this.notebook = {
            id: v4(),
            elementIds
        }

        this.evaluationStatus = "Idle";

        this.receiveMessage(NotebookElementCreatedEvent, this.getNotebookElementCreatedHandler());
        this.receiveMessage(StopSessionEvent, this.getStopSessionEventHandler());
        this.receiveMessage(StartSessionEvent, this.getStartSessionEventHandler());
        this.receiveMessage(NotebookElementMovedEvent, this.getNotebookElementMovedHandler());
        this.receiveMessage(NotebookElementDeletedEvent, this.getNotebookElementDeletedHandler());
        this.receiveMessage(EvaluateElementsCommand, this.getEvaluateElementsCommandHandler());
    }

    getElementEditor(elementId: string) {
        if (!this.elementEditorsById[elementId]) {
            this.elementEditorsById[elementId] = new ElementEditor(this.elementsById[elementId], this, this.onChange);
        }
        return this.elementEditorsById[elementId];
    }

    getEvaluateElementsCommandHandler() {
        return (event: EvaluateElementsCommand) => {
            const elementId = event.elementIds.shift();
            const elementEditor = this.elementEditorsById[elementId];

            if (!elementEditor) {
                return; // ignoring unknown elementId
            }

            this.sendMessage(new SessionStatusEvent({status: "Running"}));
            this.sendMessage(new SessionEvaluationStatusChangedEvent("Evaluating"));

            setTimeout(() => {
                try {
                    const Controls = require("./controls");

                    const result = eval(elementEditor.element.content);

                    elementEditor.setOutput(result);

                    elementEditor.sendMessage(
                        new NotebookElementEvaluationStatusEvent(
                            elementId,
                            "Idle",
                            this.executionCount++,
                            100,
                            null
                        )
                    );
                }
                catch (error) {
                    elementEditor.setOutput(makeHtml(`<div class="error">${error}</div>`).build());

                    elementEditor.sendMessage(
                        new NotebookElementEvaluationStatusEvent(
                            elementId,
                            "Idle",
                            this.executionCount++,
                            100,
                            "Evaluation failed"
                        )
                    );
                }

                this.sendMessage(new SessionEvaluationStatusChangedEvent("Idle"));
            })
        }
    }

    private getNotebookElementCreatedHandler() {
        return (event: NotebookElementCreatedEvent) => {
            const {afterElementId, elementKind, elementId, content} = event;

            this.elementsById[elementId] = {
                id: elementId,
                elementKind,
                content,
                evaluationStatus: "Idle"
            };

            this.notebook =
                {
                    ...this.notebook,
                    elementIds: insertAfter(this.notebook.elementIds, elementId, afterElementId)
                };

            this.onChange?.();

            this.sendMessage({...event, status: "Committed"});
        };
    }

    private getStopSessionEventHandler() {
        return (event: StopSessionEvent) => {
            this.executionCount = 1;
            this.sendMessage(new SessionStatusEvent({status: "Stopped"}));
        }
    }

    private getStartSessionEventHandler() {
        return (event: StartSessionEvent) => {
            setTimeout(() => {
                this.sendMessage(new SessionStatusEvent({status: "Running"}))
                this.sendMessage(new SessionEvaluationStatusChangedEvent("Idle"));
            }, 500);

        }
    }

    private getNotebookElementMovedHandler() {
        return (event: NotebookElementMovedEvent) => {
            const {elementIds, afterElementId} = event;

            this.notebook =
                {
                    ...this.notebook,
                    elementIds: moveElements(this.notebook.elementIds, elementIds, afterElementId)
                };

            this.onChange?.();

            this.sendMessage({...event, status: "Committed"});
        }
    }

    private getNotebookElementDeletedHandler() {
        return (event: NotebookElementDeletedEvent) => {
            const {elementIds} = event;

            this.notebook = {
                ...this.notebook,
                elementIds: without(this.notebook.elementIds, ...elementIds)
            }

            for(const elementId in elementIds) {
                delete this.elementsById[elementId];
            }

            this.onChange?.();

            this.sendMessage({...event, status: "Committed"});
        }
    }

}