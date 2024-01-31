import classNames from "classnames";
import { isString } from "lodash";

export type IconDef = string | {
    provider: string;
    id: string;
}

export function renderIcon(icon: IconDef) {
    let provider: string;
    let id: string;

    if (isString(icon)) {
        const provider = "sm";
        let id = icon;

        if (!id.startsWith(provider + "-")) {
            id = provider + "-" + id;
        }

        return (
            <i className={classNames("icon", provider, id)}></i>
        );
    }
    else {
        [provider, id] = [icon.provider, icon.id];

        if (!id.startsWith(provider + "-")) {
            id = provider + "-" + id;
        }

        return (
            <i className={classNames("icon", "sm", id)}></i>
        );

    }
}