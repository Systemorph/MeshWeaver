import { Collection } from "@open-smc/data/src/contract/Collection.ts";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl.ts";
import { Workspace } from "@open-smc/data/src/Workspace.ts";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference.ts";
import { map, Observable } from "rxjs";
import { EntityStore } from "@open-smc/data/src/contract/EntityStore.ts";
import { pathToUpdateAction } from "@open-smc/data/src/operators/pathToUpdateAction.ts";

export class Layout {
    private views = new Map<string, UiControl>;

    addView(area: string, control: UiControl) {
        this.views.set(area, control);
        return this;
    }

    render<T extends Collection<Collection>>(workspace: Workspace<T>, reference: LayoutAreaReference): Observable<EntityStore> {
        const ret = new Workspace<EntityStore>({reference, collections: {}})
        workspace.pipe(map(pathToUpdateAction("/collections"))).subscribe(ret);

        return ret;
    }
}