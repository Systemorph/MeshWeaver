import { expect, test, describe, jest } from "@jest/globals";
import { configureStore } from "@reduxjs/toolkit";
import { patch, workspaceReducer } from "./workspaceReducer";

import { JsonPatch } from "./contract/JsonPatch";

describe("workspace", () => {
    test("subscribe and patch", () => {
        const workspace = configureStore(
            {
                reducer: workspaceReducer,
                preloadedState: {
                    users: [
                        {
                            name: "foo"
                        }
                    ]
                }
            }
        );

        const listener = jest.fn();

        workspace.subscribe(listener);

        workspace.dispatch(
            patch(
                new JsonPatch(
                    [
                        {
                            op: "replace",
                            path: "users/0/name",
                            value: "bar"
                        }
                    ]
                )
            )
        );

        expect(listener).toHaveBeenCalledTimes(1);

        expect(workspace.getState().users[0].name).toBe("bar");
    });
});
