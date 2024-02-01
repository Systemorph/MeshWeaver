export function folderControlsResolver(folderContext: __WebpackModuleApi.RequireContext) {
    const modulesByControlName =
        folderContext.keys()
            .reduce<Record<string, string>>((result, moduleName) => {
                result[moduleName.match(/(\w+Control)\.tsx$/)[1]] = moduleName;
                return result;
            }, {});

    return (name: string) => {
        const moduleName = modulesByControlName[name]

        if (moduleName) {
            return folderContext(moduleName);
        }
    }
}