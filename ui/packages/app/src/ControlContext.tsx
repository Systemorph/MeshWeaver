import { ComponentType, createContext, PropsWithChildren, useContext, useMemo } from "react";
import { LayoutAreaModel } from "./store/appStore";
import { ControlView } from "./ControlDef";

interface ControlContextType<TView extends ControlView = ControlView> {
    layoutAreaModel: LayoutAreaModel<TView>
    parentControlContext: ControlContextType;
}

const context = createContext<ControlContextType>(null);

export const useControlContext = <TView extends ControlView>() =>
    useContext<ControlContextType<TView>>(context as any);

interface ControlContextProps {
    layoutAreaModel: LayoutAreaModel;
}

export function ControlContext({layoutAreaModel, children}: PropsWithChildren<ControlContextProps>) {
    const parentControlContext = useControlContext();

    const value = useMemo(() => {
        return {
            layoutAreaModel,
            parentControlContext,
        }
    }, [layoutAreaModel, parentControlContext]);

    return (
        <context.Provider value={value} children={children}/>
    );
}

// export function isControlContextOfType<TView>(
//     context: ControlContextType,
//     componentType: ComponentType<TView>): context is ControlContextType<TView> {
//     return context?.controlName === (componentType as Function).name;
// }
//
// export function getParentContextOfType<TView>(
//     context: ControlContextType,
//     componentType: ComponentType<TView>,
//     predicate?: (context: ControlContextType<TView>) => boolean) {
//
//     while (context) {
//         if (isControlContextOfType(context, componentType) && predicate(context)) {
//             return context;
//         }
//
//         context = context.parentControlContext;
//     }
// }
