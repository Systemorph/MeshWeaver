import { Subscription } from "rxjs";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference";
import { EntityStore } from "@open-smc/data/src/contract/EntityStore";
import { RemoteWorkspace } from "@open-smc/data/src/RemoteWorkspace";
import { EntityStoreRenderer } from "./EntityStoreRenderer";
import { MessageHub } from "@open-smc/messaging/src/api/MessageHub";

export const startSynchronization = (signalrHub: MessageHub) => {
    const subscription = new Subscription();

    const entityStore =
        new RemoteWorkspace<EntityStore>(signalrHub, new LayoutAreaReference("/"), "entityStore");

    const entityStoreRenderer = new EntityStoreRenderer(entityStore);

    subscription.add(entityStore.subscription);
    subscription.add(entityStoreRenderer.subscription);

    return () => subscription.unsubscribe();
}