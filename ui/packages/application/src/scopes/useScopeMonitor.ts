import { useEffect, useState, useCallback } from "react";
import { isObjectLike } from "lodash";
import { createScopeMonitor } from "./createScopeMonitor";
import { useMessageHub } from "../messageHub/AddHub";
import { ScopePropertyChanged } from "./scope.contract";

export function useScopeMonitor(data: unknown) {
    const [current, setCurrent] = useState(data);
    const {sendMessage, receiveMessage} = useMessageHub();

    useEffect(() => {
        setCurrent(data);
    }, [data]);

    useEffect(() => {
        if (isObjectLike(current)) {
            const setScopeProperty = createScopeMonitor<any>(current, setCurrent);
            return receiveMessage(ScopePropertyChanged, message => {
                const {scopeId, property, value} = message;
                setScopeProperty(scopeId, property, value);
            });
        }
    }, [current, receiveMessage]);

    const setScopeProperty = useCallback(
        (scopeId: string, property: string, value: unknown) => sendMessage(new ScopePropertyChanged(scopeId, property, value)),
        [sendMessage]
    );

    return {
        current,
        setScopeProperty
    };
}