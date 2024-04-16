import { Subscription } from "rxjs";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference";
import { Transport } from "@open-smc/serialization/src/Transport";
import { sampleApp } from "@open-smc/backend/src/SampleApp";
import { EntityStore } from "@open-smc/data/src/contract/EntityStore";
import { RemoteWorkspace } from "@open-smc/data/src/RemoteWorkspace";
import { EntityStoreRenderer } from "./EntityStoreRenderer";

const backendHub = new Transport(sampleApp);

export const startSynchronization = () => {
    const subscription = new Subscription();

    const entityStore =
        new RemoteWorkspace<EntityStore>(backendHub, new LayoutAreaReference("/"), "entityStore");

    const entityStoreRenderer = new EntityStoreRenderer(entityStore);

    subscription.add(entityStore.subscription);
    subscription.add(entityStoreRenderer.subscription);

    return () => subscription.unsubscribe();
}