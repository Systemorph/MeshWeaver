import { createRoot } from "react-dom/client";
import React from "react";
import { BrowserRouter, Route, Routes } from "react-router-dom";
import { AppPage } from "@open-smc/application/AppPage";
import "./index.scss";

import { Helmet } from "react-helmet";

const container = document.getElementById('root');
const root = createRoot(container!);

root.render(
    <BrowserRouter>
        <Helmet>
            <link rel="preload" href={ require('./fonts/Overpass-Regular.woff2') } as="font" type="font/woff2" crossOrigin={''} />
            <link rel="preload" href={ require('./fonts/Overpass-SemiBold.woff2') } as="font" type="font/woff2" crossOrigin={''} />
            <link rel="preload" href={ require('./fonts/Roboto-Medium.woff2') } as="font" type="font/woff2" crossOrigin={''} />
            <link rel="preload" href={ require('./fonts/Roboto-Regular.woff2') } as="font" type="font/woff2" crossOrigin={''} />
            <link rel="preload" href={ require('./fonts/sm-icons/fonts/sm-icons.woff') } as="font" type="font/woff" crossOrigin={''} />
        </Helmet>
        <Routes>
            <Route
                path="/application/:projectId/:id"
                element={<AppPage/>}
            />
        </Routes>
    </BrowserRouter>
);