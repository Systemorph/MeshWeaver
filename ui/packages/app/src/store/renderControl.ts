import { Observable, ReplaySubject, Subject, Subscription } from "rxjs";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { LayoutStackControl } from "@open-smc/layout/src/contract/controls/LayoutStackControl";
import { LayoutStackRenderer } from "./LayoutStackRenderer";
import { ItemTemplateItemRenderer, ItemTemplateRenderer } from "./ItemTemplateRenderer";
import { ControlRenderer } from "./ControlRenderer";
import { ItemTemplateControl } from "@open-smc/layout/src/contract/controls/ItemTemplateControl";
import { RendererStackTrace } from "./RendererStackTrace";
import { distinctUntilEqual } from "@open-smc/data/src/operators/distinctUntilEqual";
import { identity } from "lodash-es";

export type RenderingResult = {
    area: string;
    subscription: Subscription;
}

export function renderControl(
    area: string,
    control$: Observable<UiControl>,
    stackTrace: RendererStackTrace
): RenderingResult {
    const subject = new ReplaySubject(1);
    let lastValue: UiControl;
    let lastSubscription: Subscription;

    function getRenderer(value: UiControl) {
        if (value instanceof LayoutStackControl) {
            return new LayoutStackRenderer(area, subject as Subject<LayoutStackControl>, stackTrace);
        }

        if (value instanceof ItemTemplateControl) {
            return new ItemTemplateRenderer(area, subject as Subject<ItemTemplateControl>, stackTrace);
        }

        return new ControlRenderer(area, subject as Subject<UiControl>, stackTrace);
    }

    const subscription =
        control$
            .pipe(distinctUntilEqual())
            .subscribe(
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

    return {
        area: expandArea(area, stackTrace), 
        subscription
    }
}

export function expandArea(area: string, stackTrace: RendererStackTrace) {
    const namespace = getNamespace(stackTrace);
    return namespace ? `{${namespace}}${area}` : area;
}

function getNamespace(stackTrace: RendererStackTrace) {
    const namespace =
        stackTrace
            .map(renderer => {
                if (renderer instanceof ItemTemplateItemRenderer) {
                    return renderer.area;
                }
            })
            .filter(identity);

    return namespace.length ? `${namespace.join(", ")}` : null;
}