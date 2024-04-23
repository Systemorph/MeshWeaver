import { ControlRenderer } from "./ControlRenderer";
import { map } from "rxjs";
import { LayoutStackControl } from "@open-smc/layout/src/contract/controls/LayoutStackControl";
import { EntityReferenceCollectionRenderer } from "./EntityReferenceCollectionRenderer";

export class LayoutStackRenderer extends ControlRenderer<LayoutStackControl> {
    protected render() {
        const collectionRenderer =
            new EntityReferenceCollectionRenderer(
                this.control$.pipe(map(stack => stack?.areas)),
                this.collections,
                this
            );

        this.subscription.add(collectionRenderer.subscription);

        collectionRenderer.renderAddedReferences();

        super.render();

        collectionRenderer.renderRemovedReferences();
    }
}