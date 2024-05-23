import { Middleware } from "@reduxjs/toolkit";
import { serialize } from "@open-smc/serialization/src/serialize";

export const serializeMiddleware: Middleware =
    api =>
        next =>
            action => next(serialize(action))