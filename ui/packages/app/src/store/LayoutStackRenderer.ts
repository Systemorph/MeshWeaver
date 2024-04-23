import { bindingsToReferences, ControlRenderer } from "./ControlRenderer";
import { map } from "rxjs";
import { LayoutStackControl } from "@open-smc/layout/src/contract/controls/LayoutStackControl";
import { EntityReferenceCollectionRenderer } from "./EntityReferenceCollectionRenderer";
import { qualifyArea } from "./qualifyArea";

export class LayoutStackRenderer extends ControlRenderer<LayoutStackControl> {
    protected render() {
        const collectionRenderer =
            new EntityReferenceCollectionRenderer(
                this.control$.pipe(map(stack => stack?.areas)),
                this.stackTrace
            );

        this.subscription.add(collectionRenderer.subscription);

        collectionRenderer.renderAddedReferences();

        super.render();

        collectionRenderer.renderRemovedReferences();
    }

    protected getModel(control: LayoutStackControl) {
        if (control) {
            const componentTypeName = control.constructor.name;
            const {areas, dataContext: _, ...props} = control;

            return {
                componentTypeName,
                props: {
                    areas:
                        areas.map(reference => qualifyArea(reference.id, this.stackTrace)),
                    ...bindingsToReferences(props)
                }
            }
        }
    }
}