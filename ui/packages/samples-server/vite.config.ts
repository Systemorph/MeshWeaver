import { resolve } from 'path';
import { defineConfig } from 'vite'
import pkg from './package.json';

// https://vitejs.dev/config/
export default defineConfig({
    build: {
        minify: false,
        lib: {
            // Could also be a dictionary or array of multiple entry points
            entry: [
                './src/samplesServerPlugin.ts',
                './src/contract.ts'
            ],
            formats: ["es"],
            // name: 'MyLib',
            // the proper extensions will be added
            // fileName: 'my-lib',

        },
        rollupOptions: {
            // make sure to externalize deps that shouldn't be bundled
            // into your library
            external: [
                'rxjs',
                'fs',
                'lodash-es',
                'uuid',
                "immer"
            ],
        },
    }
});