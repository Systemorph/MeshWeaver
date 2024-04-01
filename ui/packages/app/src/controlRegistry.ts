import { ComponentType, lazy } from "react";
import { memoize } from "lodash";

export const getControlComponent = memoize((name: string) => {
    for (const resolver of controlResolvers) {
        const controlModule = resolver(name);

        if (controlModule) {
            return lazy(() => controlModule);
        }
    }
})

export function registerControlResolver(resolver: ControlModuleResolver) {
    controlResolvers.push(resolver);
}

export type ControlModuleResolver = (name: string) => Promise<{ default: ComponentType<any> }>;

const controlResolvers: ControlModuleResolver[] = [];