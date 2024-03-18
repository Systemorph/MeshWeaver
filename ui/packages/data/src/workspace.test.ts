import { expect, test, describe, jest } from "@jest/globals";
import { createWorkspace, applyPatch } from "./workspace";

describe("workspace", () => {
    test("subscribe and patch", () => {
        const workspace = createWorkspace({
            users: [
                {
                    name: "foo"
                }
            ]
        });

        const listener = jest.fn();

        workspace.subscribe(listener);

        workspace.dispatch(
            applyPatch({
                op: "replace",
                path: ["users", 0, "name"],
                value: "bar"
            })
        );

        expect(listener).toHaveBeenCalledTimes(1);

        expect(workspace.getState().users[0].name).toBe("bar");
    });
});
