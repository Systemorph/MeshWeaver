import { makeHtml } from "@open-smc/sandbox/Html";
import { makeStack } from "@open-smc/sandbox/LayoutStack";
import { openContextPanel } from "./sampleApp";
import { makeMultiselect } from "@open-smc/sandbox/Multiselect";
import { makeClassification } from "@open-smc/sandbox/Classification";
import { makeItemTemplate } from "@open-smc/sandbox/ItemTemplate";
import { makeBinding } from "@open-smc/application/dataBinding/resolveBinding";
import { makeMenuItem } from "@open-smc/sandbox/MenuItem";
import { makeCategoryFactory } from "../categoryFactory";
import { makeIcon } from "@open-smc/sandbox/Icon";
import { brandeisBlue } from "@open-smc/application/colors";
import { makeBadge } from "@open-smc/sandbox/Badge";
import { makeCheckbox } from "@open-smc/sandbox/Checkbox";

const categoryFactory = makeCategoryFactory();

const comparison = [
    {year: 2021, checked: true},
    {year: 2022, checked: false},
    {year: 2023, checked: false},
]

const imports = [
    "<strong>Item1</strong> <em>Description1</em>",
    "<strong>Item2</strong> <em>Description2</em>",
    "<strong>Item3</strong> <em>Description3</em>",
]

const dataPanel = makeStack()
    .withView(
        makeStack()
            .withView(makeHtml("<h3 class='heading'>Slice</h3>"))
            .withView(makeSlice())
    )
    .withView(
        makeStack()
            .withView(makeHtml("<h3 class='heading'>Filter</h3>"))
            .withView(makeFilter())
            .withStyle(style=>style.withMargin("24px 0 0"))
    )
    .withView(
        makeStack()
            .withView(makeHtml("<h3>Comparison</h3>"))
            .withView(makeComparison())
            .withStyle(style=>style.withMargin("24px 0 0"))
    )
    .withView(
        makeStack()
            .withView(makeHtml("<h3>Import</h3>"))
            .withView(makeImport())
            .withStyle(style=>style.withMargin("24px 0 0"))
    );

function makeFilter() {
    return makeMultiselect()
        .withCategories(categoryFactory.makeCategories(4))
        .withItemsRequestHandler((category, callback) => callback(categoryFactory.getItems(category, 8)));
}

function makeSlice() {
    return makeClassification()
        .withElementsCategory(categoryFactory.makeCategory(8))
        .withClassificationCategories(categoryFactory.makeCategories(4))
        .withItemsRequestHandler((category, callback) => callback(categoryFactory.getItems(category)));
}

function makeComparison() {
    return makeStack()
        .withView(makeHtml("<p class='p-label'>Select one or more previous years to compare to the current data.</p>"))
        .withView(
            makeItemTemplate()
                .withView(
                    makeCheckbox()
                        .withLabel(makeBinding("item.year"))
                        .withData(makeBinding("item.checked"))
                        .build()
                )
                .withSkin("VerticalPanel")
                .withData(comparison)
        )
}

function makeImport() {
    return makeItemTemplate()
        .withView(
            makeStack()
                .withView(
                    makeIcon("upload-cloud")
                        .withColor(brandeisBlue)
                        .withSize("L")
                )
                .withView(
                    makeHtml(makeBinding("item"))
                )
                .withView(
                    makeMenuItem()
                        .withTitle("Import")
                        .withColor(brandeisBlue)
                )
                .withSkin("Action")
                .build()
        )
        .withData(imports)
        .withSkin("VerticalPanel").withStyle(style=>style.withGap("16px").withMargin("12px 0 0"))
}

export function openDataPanel() {
    openContextPanel(
        makeHtml("<h2>Data</h2>"),
        dataPanel
    );
}
