import { PropsWithChildren } from "react";
import { AddHub } from "./messageHub/AddHub";
import { useTransport } from "@open-smc/application/transportContext";

export const layoutHubId = "LayoutHub";

export function LayoutHub({children}: PropsWithChildren) {
    const {layoutAddress} = useTransport();

    return (
        <AddHub address={layoutAddress} children={children} id={layoutHubId}/>
    );
}