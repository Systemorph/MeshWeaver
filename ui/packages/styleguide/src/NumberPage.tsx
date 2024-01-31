import { Sandbox } from "@open-smc/sandbox/Sandbox";
import styles from "./menuItemPage.module.scss";
import { Number } from "@open-smc/sandbox/Number";


const number = makeNumber();

export function NumberPage() {
    return (
        <div className={styles.container}>
            <div>
                <h3>Number</h3>
                <Sandbox root={number} log={true}/>
            </div>
        </div>
    );
}

function makeNumber() {
    const number = new Number(1);

    return number;
}