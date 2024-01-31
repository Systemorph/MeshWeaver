import { ComponentType, createContext, PropsWithChildren, useContext, useMemo } from "react";

export type OnViewPropertyChange = (path: string, value: unknown) => void;

interface ControlContextType<TView = any> {
    controlName: string;
    boundView: TView;
    rawView: TView;
    parentControlContext: ControlContextType;
    onChange?: OnViewPropertyChange;
}

const context = createContext<ControlContextType>(null);

export function useControlContext<TView = any>() {
    return useContext<ControlContextType<TView>>(context);
}

export function isControlContextOfType<TView>(
    context: ControlContextType,
    componentType: ComponentType<TView>): context is ControlContextType<TView> {
    return context.controlName === (componentType as Function).name;
}

export function getParentContextOfType<TView>(
    context: ControlContextType,
    componentType: ComponentType<TView>,
    predicate?: (context: ControlContextType<TView>) => boolean) {

    while (context) {
        if (isControlContextOfType(context, componentType) && predicate(context)) {
            return context;
        }

        context = context.parentControlContext;
    }
}

interface ControlContextProps<TView> {
    controlName: string;
    boundView: TView;
    rawView: TView;
    onChange: OnViewPropertyChange;
}

export function ControlContext<TView>({controlName, boundView, rawView, onChange, children}: PropsWithChildren<ControlContextProps<TView>>) {
    const parentControlContext = useControlContext();

    const value = useMemo(() => {
        return {
            controlName,
            boundView,
            rawView,
            onChange,
            parentControlContext,
        }
    }, [controlName, boundView, rawView, onChange, parentControlContext]);

    return (
        <context.Provider value={value} children={children}/>
    );
}