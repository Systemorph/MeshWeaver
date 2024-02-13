import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { playgroundServer } from "../playground-server/dist/playgroundServer.mjs";

// https://vitejs.dev/config/
export default defineConfig({
    plugins: [
        react(),
        playgroundServer()
    ],
});