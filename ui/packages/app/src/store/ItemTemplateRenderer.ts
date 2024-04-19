import { bindingsToReferences, ControlRenderer } from "./ControlRenderer";
import { distinctUntilEqual } from "@open-smc/data/src/operators/distinctUntilEqual";
import { effect } from "@open-smc/utils/src/operators/effect";
import { syncWorkspaces } from "./syncWorkspaces";
import { sliceByReference } from "@open-smc/data/src/sliceByReference";
import { Workspace } from "@open-smc/data/src/Workspace";
import { map, of, Subscription } from "rxjs";
import { ItemTemplateControl } from "@open-smc/layout/src/contract/controls/ItemTemplateControl";
import { renderControlTo } from "./renderControlTo";
import { ControlModelWorkspace } from "./ControlModelWorkspace";
import { PathReference } from "@open-smc/data/src/contract/PathReference";
import { LayoutStackRenderer } from "./LayoutStackRenderer";
import { LayoutStackControl } from "@open-smc/layout/src/contract/controls/LayoutStackControl";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";
import { uiControlType } from "./EntityStoreRenderer";

export class ItemTemplateRenderer extends ControlRenderer {
    protected render() {
        const dataWorkspace = new Workspace<unknown[]>(null);

        this.subscription.add(
            this.control$
                .pipe(map(control => control?.data))
                .pipe(distinctUntilEqual())
                .pipe(
                    effect(
                        data =>
                            syncWorkspaces(
                                sliceByReference(this.dataContextWorkspace, bindingsToReferences(data)),
                                dataWorkspace
                            )
                    )
                )
                .subscribe()
        );

        const view$ =
            this.control$
                .pipe(map(control => (control as ItemTemplateControl)?.view));

        this.subscription.add(
            dataWorkspace
                .pipe(distinctUntilEqual())
                .pipe(
                    effect(
                        data => {
                            const subscriptions =
                                data.map((item, index) => {
                                    const subscription = new Subscription();

                                    const itemDataContext =
                                        sliceByReference(dataWorkspace, new PathReference(`/${index}`));

                                    subscription.add(itemDataContext.subscription);

                                    const area = `${this.name}/${index}`;

                                    // TODO: pass itemDataContext down (4/19/2024, akravets)
                                    subscription.add(
                                        renderControlTo(
                                            new ControlModelWorkspace(view$, this.collections, area),
                                            area
                                        )
                                    );

                                    return subscription;
                                });

                            const subscription = new Subscription();
                            subscriptions.forEach(s => subscription.add(s));

                            const stack = of(
                                new LayoutStackControl(
                                    data.map(
                                        (item, index) =>
                                            new EntityReference(uiControlType, `${this.name}/${index}`)
                                    )
                                )
                            );

                            // TODO: render stack (4/19/2024, akravets)


                            return subscription;
                        }
                    )
                )
                .subscribe()
        );
    }
}