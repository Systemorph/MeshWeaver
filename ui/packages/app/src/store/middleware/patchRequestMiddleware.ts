import { Middleware } from "@reduxjs/toolkit";
import { patch, patchRequest } from "@open-smc/data/src/workspaceReducer";
import { MessageHub } from "@open-smc/message-hub/src/api/MessageHub";

export const patchRequestMiddleware = (hub: MessageHub): Middleware =>
    api =>
        next =>
            action => {
                if (patchRequest.match(action)) {
                    const {payload} = action;

                    // TODO: send patch request to backend hub,
                    // receive the response, rollback if needed (4/3/2024, akravets)
                    console.log(action);

                    return next(patch(payload));
                }
                return next(action);
            }