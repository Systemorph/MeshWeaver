import { Sandbox } from "@open-smc/sandbox/src/Sandbox";
import styles from "./menuItemPage.module.scss";
import Chance from "chance";
import { Title } from "@open-smc/sandbox/src/Title";
import { TitleSize } from "@open-smc/application/src/controls/TitleControl";

const chance = new Chance();

const sizes: TitleSize[] = [1, 2, 3, 4, 5];

export function TitlePage() {
    return (
        <div className={styles.container}>
            {
                sizes.map(size => {
                    const title = new Title(chance.company(), size);

                    return (
                        <div key={size}>
                            <h3>H{size}</h3>
                            <Sandbox root={title} log={true}/>
                        </div>
                    )
                })
            }
        </div>
    );
}