import { useDroppable } from '@dnd-kit/core';
import { SortableContext, useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { DroppableType, useClassificationSelector, useUnselectElement } from "./Classification";
import style from './classification.module.scss';
import classNames from "classnames";
import buttons from "@open-smc/ui-kit/src/components/buttons.module.scss";
import { Button } from "@open-smc/ui-kit/src/components/Button";
import { Category } from "../../application.contract";

interface CategoryProps {
    category: Category;
}

export function ClassificationCategory({category: {category, displayName}}: CategoryProps) {
    const selectedElements = useClassificationSelector('selectedElements');
    const dragging = useClassificationSelector("dragging");

    const isFromCategory = dragging?.fromCategory === category;
    const isOverCategory = dragging?.overCategory === category;

    const elements = isOverCategory && !isFromCategory
        ? (selectedElements[category] ? [...selectedElements[category], dragging.element] : [dragging.element])
        : selectedElements[category];

    const {setNodeRef} = useDroppable({
            id: category,
            data: {
                type: "Category" as DroppableType
            }
        }
    );

    return (
        <div className={style.categoryList}>
            <div className={style.category} ref={setNodeRef}>
                <h3 className={style.title}>{displayName}</h3>
                {elements?.length > 0
                    ?
                    <SortableContext items={elements}>
                        {
                            elements.map(element => {
                                    if (isFromCategory && !isOverCategory && element === dragging.element) {
                                        return (
                                            <CategoryElementPlaceholder
                                                category={category}
                                                element={element}
                                                key={element}
                                            />
                                        )
                                    }

                                    return (
                                        <SortableCategoryElement
                                            category={category}
                                            element={element}
                                            key={element}
                                        />
                                    );
                                }
                            )

                        }
                    </SortableContext>
                    : <div className={style.placeholder}>Move items here</div>
                }
            </div>
        </div>
    );
}

interface CategoryElementProps {
    category: string;
    element: string;
}

function SortableCategoryElement({category, element}: CategoryElementProps) {
    const elements = useClassificationSelector('elements');
    const unselectElement = useUnselectElement();

    const {
        attributes,
        listeners,
        setNodeRef,
        transform,
        transition,
        isDragging
    } = useSortable(
        {
            id: element,
            data: {
                type: "Element" as DroppableType,
                category
            }
        });

    const styleAttr = {
        transform: CSS.Transform.toString(transform),
        transition,
    };

    const displayName = elements[element]?.displayName;

    const className = classNames(style.sliceItem, {isDragging});

    return (
        <div className={className} ref={setNodeRef} style={styleAttr}>
            <Button icon={'sm sm-actions-drag'}
                    className={classNames(buttons.button, style.itemButton, style.dragButton)}
                    {...attributes} {...listeners}/> {/* <-- drag handle*/}
            {displayName}
            <Button icon={'sm sm-undo'}
                    className={classNames(buttons.button, style.resetButton)}
                    onClick={() => unselectElement(element)}/>
        </div>
    );
}

function CategoryElementPlaceholder({element}: CategoryElementProps) {
    const elements = useClassificationSelector('elements');
    const className = classNames(style.sliceItem, {isDragging: true});
    const unselectElement = useUnselectElement();

    const displayName = elements[element]?.displayName;

    return (
        <div className={className}>
            <Button
                icon={'sm sm-actions-drag'}
                className={classNames(buttons.button, style.itemButton, style.dragButton)}
            /> {/* <-- drag handle*/}
            {displayName}
            <Button icon={'sm sm-undo'}
                    className={classNames(buttons.button, style.resetButton)}
                    onClick={() => unselectElement(element)}/>
        </div>
    );
}