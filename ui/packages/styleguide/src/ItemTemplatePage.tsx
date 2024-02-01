import { Sandbox } from "@open-smc/sandbox/Sandbox";
import styles from "./menuItemPage.module.scss";
import { makeItemTemplate } from "@open-smc/sandbox/ItemTemplate";
import { makeMenuItem } from "@open-smc/sandbox/MenuItem";
import { makeBinding } from "@open-smc/application/dataBinding/resolveBinding";

const itemTemplate = makeItemTemplate()
    .withView(
        makeMenuItem()
            .withTitle(makeBinding("item"))
            .withClickAction(payload => alert(payload), makeBinding("item"))
            .build()
    )
    .withData(["SUB", "REV", "CMP"])
    .build();

export function ItemTemplatePage() {
    return (
        <div className={styles.container}>
            <div>
                <h3>Example</h3>
                <Sandbox root={itemTemplate} log={true}/>
            </div>
        </div>
    );
}