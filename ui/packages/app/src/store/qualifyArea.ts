import { RendererStackTrace } from "./RendererStackTrace";
import { getNamespace } from "./getNamespace";

export function qualifyArea(area: string, stackTrace: RendererStackTrace) {
    const namespace = getNamespace(stackTrace);
    return namespace ? `${namespace}.${area}` : area;
}