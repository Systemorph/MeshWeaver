import { Subscription } from "rxjs";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference";
import { EntityStore } from "@open-smc/data/src/contract/EntityStore";
import { RemoteWorkspace } from "@open-smc/data/src/RemoteWorkspace";
import { HmrClientHub } from "./HmrClientHub";
import { EntityStoreRenderer } from "@open-smc/app/src/store/EntityStoreRenderer";
import { SerializationMiddleware } from "@open-smc/middleware/src/SerializationMiddleware";

export const startSynchronization = (path: string) => {
    const subscription = new Subscription();

    const hmrClientHub = new HmrClientHub();

    const entityStore =
        new RemoteWorkspace<EntityStore>(new SerializationMiddleware(hmrClientHub), new LayoutAreaReference(path), "entityStore");

    const entityStoreRenderer = new EntityStoreRenderer(entityStore);

    subscription.add(entityStore.subscription);
    subscription.add(entityStoreRenderer.subscription);

    return () => subscription.unsubscribe();
}