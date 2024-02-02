import type {
    StackOptions,
    StackSkin,
    StackView
} from "@open-smc/application/src/controls/LayoutStackControl";
import { AreaChangedEvent } from "@open-smc/application/src/application.contract";
import { v4 } from "uuid";
import { mainWindowAreas } from "@open-smc/application/src/controls/MainWindow";
import { modalWindowAreas } from "@open-smc/application/src/controls/ModalWindow";
import {Builder} from "@open-smc/utils/src/Builder";
import {StyleBuilder} from "./StyleBuilder";
import { insertAfter } from "@open-smc/utils/src/insertAfter";
import { ControlBase, ControlBuilderBase } from "./ControlBase";

export class LayoutStack extends ControlBase implements StackView {
    skin: StackSkin;
    areas: AreaChangedEvent[];
    highlightNewAreas: boolean;
    columnCount: number;

    constructor() {
        super("LayoutStackControl");
    }

    addView(viewBuilder: ControlBuilderBase, buildFunc?: (builder: AreaChangedEventBuilder) => void) {
        const builder = new AreaChangedEventBuilder<StackOptions>().withView(viewBuilder);
        buildFunc?.(builder);

        if (!this.areas) {
            this.areas = [];
        }

        const event = builder.build()
        const {view, area, options} = event;

        const insertAfterArea = options?.insertAfter;

        const insertAfterEvent =
            insertAfterArea ? this.areas.find(a => a.area === insertAfterArea) : null;

        this.areas = insertAfter(this.areas, event, insertAfterEvent);

        this.setArea(area, view, options);
    }

    removeView(area: string) {
        const index = this.areas?.findIndex(a => a.area === area);

        if (index !== -1) {
            this.areas.splice(index, 1);
            this.setArea(area, null);
        }
    }
}

export class AreaChangedEventBuilder<TOptions = unknown> extends Builder<AreaChangedEvent<TOptions>> {
    constructor() {
        super();
        this.withArea(v4());
    }

    withArea(value: string) {
        this.data.area = value;
        return this;
    }

    withView(value: ControlBuilderBase) {
        this.data.view = value.build();
        return this;
    }

    withOptions(value: TOptions) {
        this.data.options = value;
        return this;
    }

    withStyle(buildFunc: (builder: StyleBuilder) => void) {
        const builder = new StyleBuilder();
        buildFunc(builder);
        this.data.style = builder.build();
        return this;
    }
}

export class LayoutStackBuilder extends ControlBuilderBase<LayoutStack> {
    constructor(areas?: AreaChangedEvent[]) {
        super(LayoutStack);
        this.data.areas = areas;
    }

    withView(view: ControlBuilderBase, buildFunc?: (builder: AreaChangedEventBuilder) => void) {
        const builder = new AreaChangedEventBuilder().withView(view);
        buildFunc?.(builder);

        if (!this.data.areas) {
            this.data.areas = [];
        }

        const builtArea = builder.build()

        this.data.areas.push(builtArea);

        return this;
    }

    withSkin(value: StackSkin) {
        return super.withSkin(value);
    }

    withHighlightNewAreas(value: boolean) {
        this.data.highlightNewAreas = value;
        return this;
    }

    withColumnCount(value: number) {
        this.data.columnCount = value;
        return this;
    }
}

export class SmappWindowBuilder extends LayoutStackBuilder {
    constructor() {
        super();
        this.withSkin("MainWindow");
    }

    withSideMenu(view: ControlBuilderBase) {
        return this.withView(view, (builder) => builder
            .withArea(mainWindowAreas.sideMenu)
        );
    }

    withToolbar(view: ControlBuilderBase) {
        return this.withView(view, (builder) => builder
            .withArea(mainWindowAreas.toolbar)
        );
    }

    withContextMenu(view: ControlBuilderBase) {
        return this.withView(view, (builder) => builder
            .withArea(mainWindowAreas.contextMenu));
    }

    withMain(view: ControlBuilderBase) {
        return this.withView(view, (builder) => builder
            .withArea(mainWindowAreas.main)
        );
    }

    withStatusBar(view: ControlBuilderBase) {
        return this.withView(view, (builder) => builder
            .withArea(mainWindowAreas.statusBar)
        );
    }

    withModal(view: ControlBuilderBase) {
        return this.withView(view, (builder) => builder
            .withArea(mainWindowAreas.modal)
        );
    }
}

export class ModalWindowBuilder extends LayoutStackBuilder {
    constructor() {
        super();
        this.withSkin("Modal");
    }

    withHeader(view: ControlBuilderBase) {
        return this.withView(view, (builder) => builder
            .withArea(modalWindowAreas.header)
        )
    }

    withMain(view: ControlBuilderBase) {
        return this.withView(view, (builder) => builder
            .withArea(modalWindowAreas.main)
        )
    }

    withFooter(view: ControlBuilderBase) {
        return this.withView(view, (builder) => builder
            .withArea(modalWindowAreas.footer)
        )
    }
}

export const makeStack = (areas?: AreaChangedEvent[]) => new LayoutStackBuilder(areas);

export const makeSmappWindow = () => new SmappWindowBuilder();

export const makeModalWindow = () => new ModalWindowBuilder();