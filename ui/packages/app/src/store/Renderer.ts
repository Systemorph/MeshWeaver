import { Workspace } from "@open-smc/data/src/Workspace";
import { first, identity, last } from "lodash-es";
import { RendererStackTrace } from "./RendererStackTrace";

export abstract class Renderer {
    public readonly stackTrace: RendererStackTrace;

    protected constructor(
        public readonly dataContext: Workspace,
        stackTrace: RendererStackTrace
    ) {
        this.stackTrace = stackTrace.add(this);
    }

    public get parentContext() {
        return last(
            this.stackTrace
                .slice(0, -1)
                .map(renderer => renderer.dataContext)
                .filter(identity)
        )
    }

    public get rootContext() {
        return first(this.stackTrace).dataContext;
    }
}