import { useEffect, useState, useCallback } from "react";
import { isObjectLike } from "lodash";
import { createScopeMonitor } from "./createScopeMonitor";
import { useMessageHub } from "../AddHub";
import { ScopePropertyChanged } from "./scope.contract";
import { receiveMessage } from "@open-smc/message-hub/src/receiveMessage";
import { ofType } from "../ofType";
import { sendMessage } from "@open-smc/message-hub/src/sendMessage";

export function useScopeMonitor(data: unknown) {
    const [current, setCurrent] = useState(data);
    const hub = useMessageHub();

    useEffect(() => {
        setCurrent(data);
    }, [data]);

    useEffect(() => {
        if (isObjectLike(current)) {
            const setScopeProperty = createScopeMonitor<any>(current, setCurrent);
            return receiveMessage(hub.pipe(ofType(ScopePropertyChanged)), message => {
                const {scopeId, property, value} = message;
                setScopeProperty(scopeId, property, value);
            });
        }
    }, [current, receiveMessage]);

    const setScopeProperty = useCallback(
        (scopeId: string, property: string, value: unknown) =>
            sendMessage(hub, new ScopePropertyChanged(scopeId, property, value)),
        [hub]
    );

    return {
        current,
        setScopeProperty
    };
}