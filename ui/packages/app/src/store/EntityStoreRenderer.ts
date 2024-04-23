import { distinctUntilChanged, map, Subscription } from "rxjs";
import { Workspace } from "@open-smc/data/src/Workspace";
import { EntityStore } from "@open-smc/data/src/contract/EntityStore";
import { sliceByPath } from "@open-smc/data/src/sliceByPath";
import { selectByPath } from "@open-smc/data/src/operators/selectByPath";
import { setRoot } from "./appReducer";
import { appStore } from "./appStore";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";
import { EntityReferenceCollectionRenderer } from "./EntityReferenceCollectionRenderer";
import { RendererStackTrace } from "./RendererStackTrace";
import { Renderer } from "./Renderer";
import { WorkspaceSlice } from "@open-smc/data/src/WorkspaceSlice";

export const uiControlType = (UiControl as any).$type;

export class EntityStoreRenderer extends Renderer {
    readonly subscription = new Subscription();

    constructor(entityStore: Workspace<EntityStore>) {
        super(sliceByPath(entityStore, "/collections"), new RendererStackTrace());

        this.subscription.add((this.dataContext as WorkspaceSlice).subscription);

        const rootArea$ = entityStore
            .pipe(map(selectByPath<string>("/reference/area")))
            .pipe(distinctUntilChanged());

        const collectionRenderer = new EntityReferenceCollectionRenderer(
            rootArea$
                .pipe(
                    map(rootArea =>
                        rootArea ? [new EntityReference(uiControlType, rootArea)] : [])
                ),
            this.stackTrace.add(this)
        );

        this.subscription.add(collectionRenderer.subscription);

        collectionRenderer.renderAddedReferences();

        this.subscription.add(
            rootArea$
                .subscribe(rootArea => {
                    if (rootArea) {
                        appStore.dispatch(setRoot(rootArea));
                    }
                })
        );

        collectionRenderer.renderRemovedReferences();
    }
}