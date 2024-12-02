import { defineConfig } from 'vite';

export default defineConfig({
    build: {
        emptyOutDir: true,
        minify: false,
        rollupOptions: {
            output: {
                manualChunks: function (id) {
                    if (id.includes('node_modules')) {
                        return 'vendor';
                    }
                },
                entryFileNames: 'index.mjs', // Ensure the output file is named index.mjs
                format: 'es' // Ensure the format is ES module

            }
        },
        lib: {
            entry: 'index.ts',
            fileName: 'index',
            formats: ['es']
        },
        outDir: '../wwwroot'
    },
})