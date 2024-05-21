import { bindingsToReferences, ControlRenderer } from "./ControlRenderer";
import { map } from "rxjs";
import { LayoutStackControl } from "@open-smc/layout/src/contract/controls/LayoutStackControl";
import { EntityReferenceCollectionRenderer } from "./EntityReferenceCollectionRenderer";

export class LayoutStackRenderer extends ControlRenderer<LayoutStackControl> {
    private collectionRenderer: EntityReferenceCollectionRenderer;

    protected render() {
        this.collectionRenderer =
            new EntityReferenceCollectionRenderer(
                this.control$.pipe(map(stack => stack?.areas)),
                this.stackTrace
            );

        this.subscription.add(this.collectionRenderer.subscription);

        this.collectionRenderer.renderNewAreas();

        super.render();

        this.collectionRenderer.cleanupRemovedAreas();
    }

    protected getAreaModel(area: string, control: LayoutStackControl) {
        const controlName = control.constructor.name;
        const {areas, dataContext: _, ...props} = control;

        return {
            area,
            controlName,
            props: {
                areas: this.collectionRenderer.areas,
                ...bindingsToReferences(props)
            }
        }
    }
}