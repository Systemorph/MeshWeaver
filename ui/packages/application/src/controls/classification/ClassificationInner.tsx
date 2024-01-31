import style from "./classification.module.scss";
import {
    DroppableType,
    useClassificationSelector,
    useClassificationStore, useSelectElement, useUnselectAll,
    useUnselectElement
} from "./Classification";
import { ClassificationCategory } from "./ClassificationCategory";
import {
    closestCenter,
    DndContext,
    DragEndEvent,
    DragOverEvent,
    DragOverlay,
    DragStartEvent,
} from "@dnd-kit/core";
import { ClassificationElement } from "./ClassificationElement";
import { keys } from "lodash";
import { ClassificationElements } from "./ClassificationElements";
import { Button } from "@open-smc/ui-kit/components/Button";
import classNames from "classnames";
import buttons from "@open-smc/ui-kit/components/buttons.module.scss";

export function ClassificationInner() {
    const {setState} = useClassificationStore();
    const categories = useClassificationSelector('classificationCategories');
    const selectedElements = useClassificationSelector('selectedElements');
    const elements = useClassificationSelector('elements');
    const dragging = useClassificationSelector("dragging");
    const unselectElement = useUnselectElement();
    const selectElement = useSelectElement();
    const unselectAll = useUnselectAll();

    const isEmpty = keys(selectedElements).length === 0;

    return (
        <>
            <div className={style.classificationContainer}>
                <DndContext
                    // collisionDetection={closestCenter}
                    onDragStart={handleDragStart}
                    onDragOver={handleDragOver}
                    onDragEnd={handleDragEnd}
                >
                    <ClassificationElements/>
                    <div className={style.column}>
                        {categories.map(category => <ClassificationCategory category={category}
                                                                            key={category.category}/>)}
                    </div>
                    <DragOverlay>
                        {dragging?.element ? (
                            <ClassificationElement element={elements[dragging.element]} className={'draggedItem'}/>
                        ) : null}
                    </DragOverlay>
                </DndContext>
            </div>
            <div className={style.classificationFooter}>
                <Button label={'Reset all'}
                        icon="sm sm-undo"
                        className={classNames(buttons.button, style.resetAll)}
                        onClick={unselectAll}
                        disabled={isEmpty}
                />

            </div>
        </>
    );

    function handleDragStart({active}: DragStartEvent) {
        setState(state => {
            const element = active.id as string;
            const fromCategory = active.data.current?.category;

            state.dragging = {
                element,
                fromCategory
            }
        });
    }

    function handleDragOver({over}: DragOverEvent) {
        setState(state => {
            let overElement: string;
            let overCategory: string;

            if (over) {
                const type = over.data.current?.type as DroppableType;
                overElement = type === "Element" ? over.id as string : null;
                overCategory = type === "Element" ? over.data.current.category
                    : (type === "Category" ? over.id : null);
            }

            state.dragging.overElement = overElement;
            state.dragging.overCategory = overCategory;
        });
    }

    function handleDragEnd({}: DragEndEvent) {
        const {element, fromCategory, overElement, overCategory} = dragging;

        if (overCategory) {
            if (overCategory !== fromCategory || overElement && overElement !== element){
                selectElement(overCategory, element, overElement);
            }
        }
        else if (fromCategory) {
            unselectElement(element);
        }

        setState(state => {
            state.dragging = null;
        });
    }
}
