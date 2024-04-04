import { Middleware } from "@reduxjs/toolkit";
import { patch, patchRequest } from "@open-smc/data/src/workspaceReducer";
import { MessageHub } from "@open-smc/messaging/src/api/MessageHub";
import { PatchChangeRequest } from "@open-smc/data/src/contract/PatchChangeRequest";
import { sendRequest } from "@open-smc/messaging/src/sendRequest";

export const patchRequestMiddleware = (hub: MessageHub): Middleware =>
    api =>
        next =>
            action => {
                if (patchRequest.match(action)) {
                    const {payload} = action;

                    sendRequest(hub, new PatchChangeRequest(null, null, payload))
                        .subscribe(response => {
                            if (response.status === "Failed") {
                                // TODO: rollback (4/3/2024, akravets)
                            }
                        });

                    return next(patch(payload));
                }
                return next(action);
            }