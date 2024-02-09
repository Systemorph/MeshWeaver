import { PropsWithChildren } from "react";
import { AddHub } from "./AddHub";
import { useTransport } from "@open-smc/application/src/transportContext";

export const layoutHubId = "LayoutHub";

export function LayoutHub({children}: PropsWithChildren) {
    const {layoutAddress} = useTransport();

    return (
        <AddHub address={layoutAddress} children={children} id={layoutHubId}/>
    );
}