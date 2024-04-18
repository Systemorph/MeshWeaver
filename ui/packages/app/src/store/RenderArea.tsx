import { createElement, JSX, Suspense } from "react";
import { useAppDispatch, useAppSelector } from "./hooks";
import { layoutAreaSelector } from "./appStore";
import { getControlComponent } from "../controlRegistry";
import { setProp } from "./appReducer";

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

    const {control} = layoutAreaModel;

    const onChange = (prop: string, value: any) => {
        dispatch(setProp({areaId: id, prop, value}));
    }

    const {componentTypeName, props} = control;

    const componentType = getControlComponent(componentTypeName);

    const renderedControl = (
        <Suspense fallback={<div>Loading...</div>}>
            <div className={className} key={id}>
                {
                    createElement(componentType, {
                        ...props,
                        onChange
                    })
                }
            </div>
        </Suspense>
    );

    return render ? render(renderedControl) : renderedControl;
}

export interface ControlProps {
    onChange: (prop: string, value: any) => void;
}