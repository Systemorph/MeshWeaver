import { distinctUntilChanged, map, Subscription } from "rxjs";
import { EntityStore } from "@open-smc/data/src/contract/EntityStore";
import { sliceByPath } from "@open-smc/data/src/sliceByPath";
import { selectByPath } from "@open-smc/data/src/operators/selectByPath";
import { setRootActionCreator } from "./appReducer";
import { appMessage$, appStore } from "./appStore";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";
import { AreaCollectionRenderer } from "./AreaCollectionRenderer";
import { RendererStackTrace } from "./RendererStackTrace";
import { Renderer } from "./Renderer";
import { RemoteWorkspace } from "@open-smc/data/src/RemoteWorkspace";
import { syncWorkspaces } from "./syncWorkspaces";
import { Workspace } from "@open-smc/data/src/Workspace";

export const uiControlType = (UiControl as any).$type;

export class EntityStoreRenderer extends Renderer {
    readonly subscription = new Subscription();

    constructor(
        public readonly entityStore: RemoteWorkspace<EntityStore>
    ) {
        super(new RendererStackTrace());

        this.dataContext = new Workspace(null);
        
        const dataContext = sliceByPath(entityStore, "/collections")

        this.subscription.add(dataContext.subscription);
        this.subscription.add(syncWorkspaces(dataContext, this.dataContext));

        this.subscription.add(
            appMessage$
                .subscribe(message => {
                    entityStore.post(message);
                })
        )

        const rootArea$ = entityStore
            .pipe(map(selectByPath<string>("/reference/area")))
            .pipe(distinctUntilChanged());

        const collectionRenderer = new AreaCollectionRenderer(
            rootArea$
                .pipe(
                    map(rootArea =>
                        rootArea ? [new EntityReference(uiControlType, rootArea)] : [])
                ),
            this.stackTrace
        );

        this.subscription.add(collectionRenderer.subscription);

        collectionRenderer.renderNewAreas();

        this.subscription.add(
            rootArea$
                .subscribe(rootArea => {
                    if (rootArea) {
                        appStore.dispatch(setRootActionCreator(rootArea));
                    }
                })
        );

        collectionRenderer.cleanupRemovedAreas();
    }
}