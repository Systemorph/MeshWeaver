import { bindingsToReferences, ControlRenderer } from "./ControlRenderer";
import { map, Observable } from "rxjs";
import { LayoutStackControl } from "@open-smc/layout/src/contract/controls/LayoutStackControl";
import { AreaCollectionRenderer } from "./AreaCollectionRenderer";
import { RendererStackTrace } from "./RendererStackTrace";

export class LayoutStackRenderer extends ControlRenderer<LayoutStackControl> {
    private collectionRenderer: AreaCollectionRenderer;

    constructor(
        area: string,
        control$: Observable<LayoutStackControl>,
        stackTrace: RendererStackTrace
    ) {
        super(area, control$, stackTrace);

        this.collectionRenderer =
            new AreaCollectionRenderer(
                this.control$.pipe(map(stack => stack?.areas)),
                this.stackTrace
            );

        this.subscription.add(this.collectionRenderer.subscription);

        this.collectionRenderer.renderNewAreas();
        super.renderAreaModel();
        this.collectionRenderer.cleanupRemovedAreas();
    }

    protected renderAreaModel() {
    }

    protected getAreaModel(control: LayoutStackControl) {
        const controlName = control.constructor.name;
        const { areas, dataContext: _, ...props } = control;

        return {
            area: this.expandedArea,
            controlName,
            props: {
                areas: this.collectionRenderer.areas,
                ...bindingsToReferences(props)
            }
        }
    }
}