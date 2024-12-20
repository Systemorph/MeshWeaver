import type {
    StackOptions,
    StackSkin,
    StackView
} from "@open-smc/application/src/controls/LayoutStackControl";
import { AreaChangedEvent } from "@open-smc/application/src/contract/application.contract";
import { v4 } from "uuid";
import { modalWindowAreas } from "@open-smc/application/src/controls/ModalWindow";
import {Builder} from "@open-smc/utils/src/Builder";
// import {StyleBuilder} from "./StyleBuilder";
import { insertAfter } from "@open-smc/utils/src/insertAfter";
import { ControlBase } from "./ControlBase";
import { mainWindowAreas } from "@open-smc/application/src/controls/mainWindowApi";
import { Style } from "packages/application/src/contract/controls/Style";

export class LayoutStack extends ControlBase implements StackView {
    skin: StackSkin;
    readonly areas: AreaChangedEvent[] = [];
    highlightNewAreas: boolean;
    columnCount: number;

    constructor(id?: string) {
        super("LayoutStackControl", id);
    }

    withView(control: ControlBase, area = v4(), options?: any, style?: Style) {
        this.addChildHub(control, control.address);
        this.areas.push(new AreaChangedEvent(area, control, options, style));
        return this;
    }

    withSkin(value: StackSkin) {
        return super.withSkin(value);
    }

    withHighlightNewAreas(value: boolean) {
        this.highlightNewAreas = value;
        return this;
    }

    withColumnCount(value: number) {
        this.columnCount = value;
        return this;
    }

    // addView(view: ControlBase, buildFunc?: (builder: AreaChangedEventBuilder) => void) {
    //     const are
    //     const builder = new AreaChangedEventBuilder<StackOptions>().withView(viewBuilder);
    //     buildFunc?.(builder);
    //
    //     if (!this.areas) {
    //         this.areas = [];
    //     }
    //
    //     const event = builder.build()
    //     const {view, area, options} = event;
    //
    //     const insertAfterArea = options?.insertAfter;
    //
    //     const insertAfterEvent =
    //         insertAfterArea ? this.areas.find(a => a.area === insertAfterArea) : null;
    //
    //     this.areas = insertAfter(this.areas, event, insertAfterEvent);
    //
    //     this.sendMessage(new AreaChangedEvent(area, view, options));
    // }
    //
    // removeView(area: string) {
    //     const index = this.areas?.findIndex(a => a.area === area);
    //
    //     if (index !== -1) {
    //         this.areas.splice(index, 1);
    //         this.setArea(area, null);
    //     }
    // }
}

// export class AreaChangedEventBuilder<TOptions = unknown> extends Builder<AreaChangedEvent<TOptions>> {
//     constructor() {
//         super();
//         this.withArea(v4());
//     }
//
//     withArea(value: string) {
//         this.data.area = value;
//         return this;
//     }
//
//     withView(value: ControlBuilderBase) {
//         this.data.view = value.build();
//         return this;
//     }
//
//     withOptions(value: TOptions) {
//         this.data.options = value;
//         return this;
//     }
//
//     withStyle(buildFunc: (builder: StyleBuilder) => void) {
//         const builder = new StyleBuilder();
//         buildFunc(builder);
//         this.data.style = builder.build();
//         return this;
//     }
// }

export class MainWindowStack extends LayoutStack {
    constructor(id?: string) {
        super(id);
        this.withSkin("MainWindow");
    }

    withSideMenu(control: ControlBase) {
        return this.withView(control, mainWindowAreas.sideMenu);
    }

    withToolbar(control: ControlBase) {
        return this.withView(control, mainWindowAreas.toolbar);
    }

    withContextMenu(control: ControlBase) {
        return this.withView(control , mainWindowAreas.contextMenu);
    }

    withMain(control: ControlBase) {
        return this.withView(control, mainWindowAreas.main);
    }

    withStatusBar(control: ControlBase) {
        return this.withView(control, mainWindowAreas.statusBar);
    }

    withModal(control: ControlBase) {
        return this.withView(control, mainWindowAreas.modal);
    }
}

//
// export class ModalWindowBuilder extends LayoutStackBuilder {
//     constructor() {
//         super();
//         this.withSkin("Modal");
//     }
//
//     withHeader(view: ControlBuilderBase) {
//         return this.withView(view, (builder) => builder
//             .withArea(modalWindowAreas.header)
//         )
//     }
//
//     withMain(view: ControlBuilderBase) {
//         return this.withView(view, (builder) => builder
//             .withArea(modalWindowAreas.main)
//         )
//     }
//
//     withFooter(view: ControlBuilderBase) {
//         return this.withView(view, (builder) => builder
//             .withArea(modalWindowAreas.footer)
//         )
//     }
// }

export const makeStack = (id?: string) => new LayoutStack(id);
//
// export const makeSmappWindow = () => new SmappWindowBuilder();
//
// export const makeModalWindow = () => new ModalWindowBuilder();