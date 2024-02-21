import { keys } from "lodash";
import { ControlModule } from "./ControlModule";

export function viteFolderControlsResolver(modules: Record<string, () => Promise<ControlModule>>) {
    const modulesByControlName =
        keys(modules)
            .reduce<Record<string, () => Promise<ControlModule>>>((result, moduleName) => {
                result[moduleName.match(/(\w+Control)\.tsx$/)[1]] = modules[moduleName];
                return result;
            }, {});

    return (name: string) => modulesByControlName[name]();
}