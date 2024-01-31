import { describe, expect, test, jest } from '@jest/globals';
import { createStore, Listener } from "./store";

describe('basic', () => {
    test('simple property change', () => {
        const store = createStore({
            name: "a"
        });

        const fn = jest.fn();

        store.subscribe("name", fn);

        store.setState(state => {
            state.name = "b";
        });

        expect(fn).toHaveBeenCalledWith("b");
    });

    test('nested property change', () => {
        const initialState = {
            users: [
                {
                    name: "a"
                },
                {
                    name: "b"
                }
            ]
        };

        const store = createStore(initialState);

        const fn = jest.fn<Listener<(typeof initialState)['users']>>();

        store.subscribe("users", fn);

        store.setState(state => {
            state.users[0].name = "aa";
        });

        expect(fn).toHaveBeenCalledTimes(1);

        const users = fn.mock.lastCall[0];

        expect(users).not.toBe(initialState.users);

        expect(users[0]).not.toBe(initialState.users[0]);

        expect(users[0]).toEqual({
            name: "aa"
        });

        expect(users[1]).toBe(initialState.users[1]);
    });
});