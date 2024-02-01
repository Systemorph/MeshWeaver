import { useSelectedElements, useClassificationSelector, DroppableType } from "./Classification";
import { useDroppable } from "@dnd-kit/core";
import { keys } from "lodash";
import { DraggableItem, ClassificationElement } from "./ClassificationElement";
import style from "./classification.module.scss";
import { v4 } from "uuid";

const droppableId = v4();

export function ClassificationElements() {
    const elements = useClassificationSelector('elements')
    const dragging = useClassificationSelector("dragging");
    const selectedElements = useSelectedElements();

    const {setNodeRef} = useDroppable({
        id: droppableId,
        data: {
            type: "AllElements" as DroppableType
        }
    });

    return (
        <div className={style.elementsList} ref={setNodeRef}>
            {!elements && <div>Loading...</div>}
            {
                elements && keys(elements).map(systemName => {
                    const element = elements[systemName];

                    if (selectedElements.includes(systemName) || systemName === dragging?.element) {
                        return (
                            <ClassificationElement
                                element={element}
                                handle={false}
                                className={'selected'}
                                key={systemName}
                            />
                        );
                    }

                    return (
                        <DraggableItem
                            element={element}
                            key={element.systemName}
                        />
                    );
                })
            }
        </div>
    );
}
