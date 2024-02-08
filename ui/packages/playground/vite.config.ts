import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { playgroundServer } from "./src/server/playgroundServer";

// https://vitejs.dev/config/
export default defineConfig({
    plugins: [
        react(),
        playgroundServer()
    ],
});