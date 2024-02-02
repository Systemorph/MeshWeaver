import { makeCheckbox } from "@open-smc/sandbox/src/Checkbox";
import { Sandbox } from "@open-smc/sandbox/src//Sandbox";
import { makeItemTemplate } from "@open-smc/sandbox/src/ItemTemplate";
import { makeBinding } from "@open-smc/application/src/dataBinding/resolveBinding";
import { v4 } from "uuid";

const itemTemplate =
    makeItemTemplate()
        .withView(
            makeCheckbox()
                .withId(makeBinding("item.id"))
                .withLabel(makeBinding("item.label"))
                .withData(makeBinding("item.data"))
                .isReadOnly(makeBinding("item.isReadOnly"))
                .build()
        )
        .withSkin("HorizontalPanel")
        .withData(makeBinding("scopes"))
        .withDataContext({
            scopes: [
                {
                    $scopeId: "123",
                    id: v4(),
                    data: true,
                    label: "Checkbox",
                    isReadOnly: false,
                },
                {
                    $scopeId: "123",
                    id: v4(),
                    data: true,
                    label: "Checkbox disabled",
                    isReadOnly: true,
                },
            ]
        })
        .build();

export function CheckboxPage() {
    return (
        <>
            <h3>Sample checkbox</h3>
            <Sandbox root={itemTemplate} log={true}/>
        </>
    );
}
