import { Observable, ReplaySubject, Subscription } from "rxjs";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { LayoutStackControl } from "@open-smc/layout/src/contract/controls/LayoutStackControl";
import { LayoutStackRenderer } from "./LayoutStackRenderer";
import { ItemTemplateRenderer } from "./ItemTemplateRenderer";
import { ControlRenderer } from "./ControlRenderer";
import { ItemTemplateControl } from "@open-smc/layout/src/contract/controls/ItemTemplateControl";
import { Renderer } from "./Renderer";

export function renderControl(
    control$: Observable<UiControl>,
    collections: any,
    area: string,
    parentRenderer: Renderer
) {
    const subject = new ReplaySubject<UiControl>(1);
    let lastValue: UiControl;
    let lastSubscription: Subscription;
    let branches

    function getRenderer(value: UiControl) {
        if (value instanceof LayoutStackControl) {
            return new LayoutStackRenderer(subject as any, collections, area, parentRenderer);
        }

        if (value instanceof ItemTemplateControl) {
            return new ItemTemplateRenderer(subject as any, collections, area, parentRenderer);
        }

        return new ControlRenderer(subject, collections, area, parentRenderer);
    }

    const subscription =
        control$.subscribe(
            value => {
                if (value?.constructor !== lastValue?.constructor) {
                    lastValue = value;
                    lastSubscription?.unsubscribe();

                    if (value) {
                        const renderer = getRenderer(value);
                        lastSubscription = renderer.subscription;
                    }
                }
                subject.next(value);
            }
        );

    subscription.add(() => {
        if (lastSubscription) {
            lastSubscription.unsubscribe();
        }
    })

    return subscription;
}