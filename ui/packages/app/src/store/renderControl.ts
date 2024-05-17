import { Observable, ReplaySubject, Subject, Subscription } from "rxjs";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { LayoutStackControl } from "@open-smc/layout/src/contract/controls/LayoutStackControl";
import { LayoutStackRenderer } from "./LayoutStackRenderer";
import { ItemTemplateRenderer } from "./ItemTemplateRenderer";
import { ControlRenderer } from "./ControlRenderer";
import { ItemTemplateControl } from "@open-smc/layout/src/contract/controls/ItemTemplateControl";
import { RendererStackTrace } from "./RendererStackTrace";
import { qualifyArea } from "./qualifyArea";

export function renderControl(
    area: string,
    control$: Observable<UiControl>,
    stackTrace: RendererStackTrace
) {
    const subject = new ReplaySubject<UiControl>(1);
    let lastValue: UiControl;
    let lastSubscription: Subscription;

    area = qualifyArea(area, stackTrace);

    function getRenderer(value: UiControl) {
        if (value instanceof LayoutStackControl) {
            return new LayoutStackRenderer(area, subject as any, stackTrace);
        }

        if (value instanceof ItemTemplateControl) {
            return new ItemTemplateRenderer(area, subject as any, stackTrace);
        }

        return new ControlRenderer(area, subject, stackTrace);
    }

    const subscription =
        control$.subscribe(
            value => {
                if (value?.constructor !== lastValue?.constructor) {
                    lastValue = value;
                    lastSubscription?.unsubscribe();
                    lastSubscription = null;

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