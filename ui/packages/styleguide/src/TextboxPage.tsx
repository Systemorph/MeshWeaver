import { Sandbox } from "@open-smc/sandbox/src/Sandbox";
import styles from "./menuItemPage.module.scss";
import { Textbox } from "@open-smc/sandbox/src/Textbox";


const textbox = makeTextbox();

export function TextboxPage() {
    return (
        <div className={styles.container}>
            <div>
                <h3>Textbox</h3>
                <Sandbox root={textbox} log={true}/>
            </div>
        </div>
    );
}

function makeTextbox() {
    const textbox = new Textbox('test');

    return textbox;
}