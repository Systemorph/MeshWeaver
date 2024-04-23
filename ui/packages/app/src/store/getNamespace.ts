import { RendererStackTrace } from "./RendererStackTrace";
import { ItemTemplateItemRenderer } from "./ItemTemplateRenderer";
import { identity } from "lodash-es";

export function getNamespace(stackTrace: RendererStackTrace) {
    const namespace =
        stackTrace
            .map(renderer => {
                if (renderer instanceof ItemTemplateItemRenderer) {
                    return renderer.area;
                }
            })
            .filter(identity);

    return namespace.length ? `[${namespace.join("@")}]` : null;
}