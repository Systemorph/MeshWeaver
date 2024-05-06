import { createElement, JSX, Suspense } from "react";
import { useAppSelector } from "./hooks";
import { layoutAreaSelector } from "./appStore";
import { getControlComponent } from "../controlRegistry";

interface RenderAreaProps {
    id: string;
    className?: string;
    render?: (renderedControl: JSX.Element) => JSX.Element;
}

export function RenderArea({id, className, render}: RenderAreaProps) {
    const layoutAreaModel = useAppSelector(layoutAreaSelector(id));

    if (!layoutAreaModel?.controlName) {
        return null;
    }

    const {controlName} = layoutAreaModel;

    const componentType = getControlComponent(controlName);

    const renderedControl = (
        <Suspense fallback={<div>Loading...</div>}>
            <div className={className} key={id}>
                {
                    createElement(componentType, layoutAreaModel)
                }
            </div>
        </Suspense>
    );

    return render ? render(renderedControl) : renderedControl;
}