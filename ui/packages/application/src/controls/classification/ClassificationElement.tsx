import { useDraggable } from "@dnd-kit/core";
import { forwardRef, HTMLAttributes } from "react";
import style from './classification.module.scss';
import classNames from "classnames";
import buttons from "@open-smc/ui-kit/components/buttons.module.scss";
import { Named } from "../../application.contract";
import {Button} from "@open-smc/ui-kit/components";

interface ItemProps {
    element: Named;
    handle?: boolean;
}

export function ClassificationElement({
                                          element: {displayName},
                                          handle,
                                          className,
                                          ...props
                                      }: ItemProps & HTMLAttributes<HTMLDivElement>) {
    const dragIcon = handle === false ? null : <DragIcon/>;

    className = classNames(style.sliceItem, className);

    return (
        <div className={className} {...props}>
            {dragIcon}
            {displayName}
        </div>
    );
}

interface DraggableItemProps {
    element: Named;
}

export function DraggableItem({
                                  element: {systemName, displayName},
                                  className,
                                  ...props
                              }: DraggableItemProps & HTMLAttributes<HTMLDivElement>) {
    const {attributes, listeners, setNodeRef, setActivatorNodeRef, isDragging} = useDraggable({
        id: systemName
    });

    const dragIcon = isDragging ? null : <DragIcon ref={setActivatorNodeRef} {...listeners} {...attributes}/>;

    className = classNames(style.sliceItem, {isDragging}, className);

    return (
        <div ref={setNodeRef} className={className} {...props}>
            {dragIcon}
            {displayName}
        </div>
    );
}

const DragIcon = forwardRef<HTMLButtonElement>((props, ref) => {
    return <Button {...props}
                   ref={ref as any}
                   icon={'sm sm-actions-drag'}
                   className={classNames(buttons.button, style.itemButton, style.dragButton)}/>;
});
