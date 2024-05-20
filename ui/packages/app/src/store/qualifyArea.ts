import { identity } from "lodash-es";
import { ItemTemplateItemRenderer } from "./ItemTemplateRenderer";
import { RendererStackTrace } from "./RendererStackTrace";

export function qualifyArea(area: string, stackTrace: RendererStackTrace) {
    const namespace = getNamespace(stackTrace);
    return namespace ? `[${namespace}${area}]` : area;
}

function getNamespace(stackTrace: RendererStackTrace) {
    const namespace =
        stackTrace
            .map(renderer => {
                if (renderer instanceof ItemTemplateItemRenderer) {
                    return renderer.area;
                }
            })
            .filter(identity);

    return namespace.length ? `${namespace.join(", ")}` : null;
}