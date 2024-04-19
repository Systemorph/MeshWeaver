import { ControlRenderer, nestedAreas } from "./ControlRenderer";
import { map, Observable } from "rxjs";
import { AreaCollectionRenderer } from "./AreaCollectionRenderer";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";

export class LayoutStackRenderer extends ControlRenderer {
    protected render() {
        super.render();

        const nestedAreas$ =
            this.control$.pipe(map(nestedAreas));

        this.renderNestedAreas(nestedAreas$);
    }

    protected renderNestedAreas(nestedAreas$: Observable<EntityReference[]>) {
        const nestedAreasRenderer =
            new AreaCollectionRenderer(nestedAreas$, this.collections);

        this.subscription.add(nestedAreasRenderer.subscription);
    }
}