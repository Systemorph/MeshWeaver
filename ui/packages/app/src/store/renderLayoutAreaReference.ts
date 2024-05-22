import { Subscription } from "rxjs";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference";
import { EntityStore } from "@open-smc/data/src/contract/EntityStore";
import { RemoteWorkspace } from "@open-smc/data/src/RemoteWorkspace";
import { EntityStoreRenderer } from "./EntityStoreRenderer";
import { MessageHub } from "@open-smc/messaging/src/MessageHub";

export const renderLayoutAreaReference = (
    uiHub: MessageHub,
    reference: LayoutAreaReference
) => {
    const subscription = new Subscription();

    const entityStoreWorkspace = new RemoteWorkspace<EntityStore>(
        uiHub,
        reference,
        "entityStore"
    );

    const entityStoreRenderer = new EntityStoreRenderer(entityStoreWorkspace);

    subscription.add(entityStoreWorkspace.subscription);
    subscription.add(entityStoreRenderer.subscription);

    return () => subscription.unsubscribe();
}