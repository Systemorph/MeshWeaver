import { createElement, JSX } from "react";
import { useAppDispatch, useAppSelector } from "./hooks";
import { layoutAreaSelector, setProp } from "./store";
import { getControlComponent } from "../controlRegistry";

interface RenderAreaProps {
    id: string;
    className?: string;
    render?: (renderedControl: JSX.Element) => JSX.Element;
}

export function RenderArea({id, className, render}: RenderAreaProps) {
    const layoutAreaModel = useAppSelector(layoutAreaSelector(id));
    const dispatch = useAppDispatch();

    if (!layoutAreaModel?.control) {
        return null;
    }

    const {control, style} = layoutAreaModel;

    const onChange = (prop: string, value: any) => {
        dispatch(setProp({areaId: id, prop, value}));
    }

    const {componentTypeName, props} = control;

    const componentType = getControlComponent(componentTypeName);

    const renderedControl = (
        <div style={style} className={className} key={id}>
            {
                createElement(componentType, {
                    ...props,
                    onChange
                })
            }
        </div>
    );

    return render ? render(renderedControl) : renderedControl;
}

export interface ControlProps {
    onChange: (prop: string, value: any) => void;
}