// vite.config.mts
import { defineConfig } from "file:///C:/dev/MeshWeaver/src/MeshWeaver.Blazor/StaticAssets/node_modules/vite/dist/node/index.js";
import { viteStaticCopy } from "file:///C:/dev/MeshWeaver/src/MeshWeaver.Blazor/StaticAssets/node_modules/vite-plugin-static-copy/dist/index.js";
import commonjs from "file:///C:/dev/MeshWeaver/src/MeshWeaver.Blazor/StaticAssets/node_modules/@rollup/plugin-commonjs/dist/es/index.js";
var vite_config_default = defineConfig({
  plugins: [
    viteStaticCopy({
      targets: [
        {
          src: "node_modules/highlight.js/styles/*.min.css",
          dest: "./css/",
          rename: (name, extension) => `${name}.${extension}`
          // Preserve the full filename
        },
        {
          src: "node_modules/@primer/css/dist/markdown.css",
          dest: "./css/",
          rename: (name, extension) => `${name}.${extension}`
          // Preserve the full filename
        },
        {
          src: "node_modules/mermaid/dist/mermaid.min.js",
          dest: "./"
        },
        {
          src: "node_modules/mathjax-full/es5/**/*",
          dest: "./mathjax/"
        }
      ],
      structured: false
      // This will prevent maintaining folder structure
    }),
    commonjs({
      include: [/mathjax-full/],
      transformMixedEsModules: true,
      requireReturnsDefault: "auto"
    })
  ],
  build: {
    emptyOutDir: true,
    minify: false,
    lib: {
      entry: [
        "highlight.ts",
        "mermaid.ts",
        "mathjax.ts"
      ],
      formats: ["es"]
    },
    outDir: "../wwwroot",
    rollupOptions: {
      external: [/^mathjax-full\/es5\//]
    }
  },
  resolve: {
    alias: {
      "mathjax-full": "mathjax-full/es5"
    }
  }
});
export {
  vite_config_default as default
};
//# sourceMappingURL=data:application/json;base64,ewogICJ2ZXJzaW9uIjogMywKICAic291cmNlcyI6IFsidml0ZS5jb25maWcubXRzIl0sCiAgInNvdXJjZXNDb250ZW50IjogWyJjb25zdCBfX3ZpdGVfaW5qZWN0ZWRfb3JpZ2luYWxfZGlybmFtZSA9IFwiQzpcXFxcZGV2XFxcXE1lc2hXZWF2ZXJcXFxcc3JjXFxcXE1lc2hXZWF2ZXIuQmxhem9yXFxcXFN0YXRpY0Fzc2V0c1wiO2NvbnN0IF9fdml0ZV9pbmplY3RlZF9vcmlnaW5hbF9maWxlbmFtZSA9IFwiQzpcXFxcZGV2XFxcXE1lc2hXZWF2ZXJcXFxcc3JjXFxcXE1lc2hXZWF2ZXIuQmxhem9yXFxcXFN0YXRpY0Fzc2V0c1xcXFx2aXRlLmNvbmZpZy5tdHNcIjtjb25zdCBfX3ZpdGVfaW5qZWN0ZWRfb3JpZ2luYWxfaW1wb3J0X21ldGFfdXJsID0gXCJmaWxlOi8vL0M6L2Rldi9NZXNoV2VhdmVyL3NyYy9NZXNoV2VhdmVyLkJsYXpvci9TdGF0aWNBc3NldHMvdml0ZS5jb25maWcubXRzXCI7XHVGRUZGaW1wb3J0IHsgZGVmaW5lQ29uZmlnIH0gZnJvbSAndml0ZSc7XHJcbmltcG9ydCB7IHZpdGVTdGF0aWNDb3B5IH0gZnJvbSAndml0ZS1wbHVnaW4tc3RhdGljLWNvcHknO1xyXG5pbXBvcnQgY29tbW9uanMgZnJvbSAnQHJvbGx1cC9wbHVnaW4tY29tbW9uanMnO1xyXG5cclxuZXhwb3J0IGRlZmF1bHQgZGVmaW5lQ29uZmlnKHtcclxuICAgIHBsdWdpbnM6IFtcclxuICAgICAgICB2aXRlU3RhdGljQ29weSh7XHJcbiAgICAgICAgICAgIHRhcmdldHM6IFtcclxuICAgICAgICAgICAgICAgIHtcclxuICAgICAgICAgICAgICAgICAgICBzcmM6ICdub2RlX21vZHVsZXMvaGlnaGxpZ2h0LmpzL3N0eWxlcy8qLm1pbi5jc3MnLFxyXG4gICAgICAgICAgICAgICAgICAgIGRlc3Q6ICcuL2Nzcy8nLFxyXG4gICAgICAgICAgICAgICAgICAgIHJlbmFtZTogKG5hbWUsIGV4dGVuc2lvbikgPT4gYCR7bmFtZX0uJHtleHRlbnNpb259YCAgLy8gUHJlc2VydmUgdGhlIGZ1bGwgZmlsZW5hbWVcclxuICAgICAgICAgICAgICAgIH0sXHJcbiAgICAgICAgICAgICAgICB7XHJcbiAgICAgICAgICAgICAgICAgICAgc3JjOiAnbm9kZV9tb2R1bGVzL0BwcmltZXIvY3NzL2Rpc3QvbWFya2Rvd24uY3NzJyxcclxuICAgICAgICAgICAgICAgICAgICBkZXN0OiAnLi9jc3MvJyxcclxuICAgICAgICAgICAgICAgICAgICByZW5hbWU6IChuYW1lLCBleHRlbnNpb24pID0+IGAke25hbWV9LiR7ZXh0ZW5zaW9ufWAgIC8vIFByZXNlcnZlIHRoZSBmdWxsIGZpbGVuYW1lXHJcbiAgICAgICAgICAgICAgICB9LFxyXG4gICAgICAgICAgICAgICAge1xyXG4gICAgICAgICAgICAgICAgICAgIHNyYzogJ25vZGVfbW9kdWxlcy9tZXJtYWlkL2Rpc3QvbWVybWFpZC5taW4uanMnLFxyXG4gICAgICAgICAgICAgICAgICAgIGRlc3Q6ICcuLydcclxuICAgICAgICAgICAgICAgIH0sXHJcbiAgICAgICAgICAgICAgICB7XHJcbiAgICAgICAgICAgICAgICAgICAgc3JjOiAnbm9kZV9tb2R1bGVzL21hdGhqYXgtZnVsbC9lczUvKiovKicsXHJcbiAgICAgICAgICAgICAgICAgICAgZGVzdDogJy4vbWF0aGpheC8nXHJcbiAgICAgICAgICAgICAgICB9XHJcbiAgICAgICAgICAgIF0sXHJcbiAgICAgICAgICAgIHN0cnVjdHVyZWQ6IGZhbHNlICAvLyBUaGlzIHdpbGwgcHJldmVudCBtYWludGFpbmluZyBmb2xkZXIgc3RydWN0dXJlXHJcbiAgICAgICAgfSksXHJcbiAgICAgICAgY29tbW9uanMoe1xyXG4gICAgICAgICAgICBpbmNsdWRlOiBbL21hdGhqYXgtZnVsbC9dLFxyXG4gICAgICAgICAgICB0cmFuc2Zvcm1NaXhlZEVzTW9kdWxlczogdHJ1ZSxcclxuICAgICAgICAgICAgcmVxdWlyZVJldHVybnNEZWZhdWx0OiAnYXV0bydcclxuICAgICAgICB9KVxyXG4gICAgXSxcclxuICAgIGJ1aWxkOiB7XHJcbiAgICAgICAgZW1wdHlPdXREaXI6IHRydWUsXHJcbiAgICAgICAgbWluaWZ5OiBmYWxzZSxcclxuICAgICAgICBsaWI6IHtcclxuICAgICAgICAgICAgZW50cnk6IFtcclxuICAgICAgICAgICAgICAgICdoaWdobGlnaHQudHMnLFxyXG4gICAgICAgICAgICAgICAgJ21lcm1haWQudHMnLFxyXG4gICAgICAgICAgICAgICAgJ21hdGhqYXgudHMnXHJcbiAgICAgICAgICAgIF0sXHJcbiAgICAgICAgICAgIGZvcm1hdHM6IFsnZXMnXVxyXG4gICAgICAgIH0sXHJcbiAgICAgICAgb3V0RGlyOiAnLi4vd3d3cm9vdCcsXHJcbiAgICAgICAgcm9sbHVwT3B0aW9uczoge1xyXG4gICAgICAgICAgICBleHRlcm5hbDogWy9ebWF0aGpheC1mdWxsXFwvZXM1XFwvL11cclxuICAgICAgICB9XHJcbiAgICB9LFxyXG4gICAgcmVzb2x2ZToge1xyXG4gICAgICAgIGFsaWFzOiB7XHJcbiAgICAgICAgICAgICdtYXRoamF4LWZ1bGwnOiAnbWF0aGpheC1mdWxsL2VzNSdcclxuICAgICAgICB9XHJcbiAgICB9XHJcbn0pIl0sCiAgIm1hcHBpbmdzIjogIjtBQUE2VixTQUFTLG9CQUFvQjtBQUMxWCxTQUFTLHNCQUFzQjtBQUMvQixPQUFPLGNBQWM7QUFFckIsSUFBTyxzQkFBUSxhQUFhO0FBQUEsRUFDeEIsU0FBUztBQUFBLElBQ0wsZUFBZTtBQUFBLE1BQ1gsU0FBUztBQUFBLFFBQ0w7QUFBQSxVQUNJLEtBQUs7QUFBQSxVQUNMLE1BQU07QUFBQSxVQUNOLFFBQVEsQ0FBQyxNQUFNLGNBQWMsR0FBRyxJQUFJLElBQUksU0FBUztBQUFBO0FBQUEsUUFDckQ7QUFBQSxRQUNBO0FBQUEsVUFDSSxLQUFLO0FBQUEsVUFDTCxNQUFNO0FBQUEsVUFDTixRQUFRLENBQUMsTUFBTSxjQUFjLEdBQUcsSUFBSSxJQUFJLFNBQVM7QUFBQTtBQUFBLFFBQ3JEO0FBQUEsUUFDQTtBQUFBLFVBQ0ksS0FBSztBQUFBLFVBQ0wsTUFBTTtBQUFBLFFBQ1Y7QUFBQSxRQUNBO0FBQUEsVUFDSSxLQUFLO0FBQUEsVUFDTCxNQUFNO0FBQUEsUUFDVjtBQUFBLE1BQ0o7QUFBQSxNQUNBLFlBQVk7QUFBQTtBQUFBLElBQ2hCLENBQUM7QUFBQSxJQUNELFNBQVM7QUFBQSxNQUNMLFNBQVMsQ0FBQyxjQUFjO0FBQUEsTUFDeEIseUJBQXlCO0FBQUEsTUFDekIsdUJBQXVCO0FBQUEsSUFDM0IsQ0FBQztBQUFBLEVBQ0w7QUFBQSxFQUNBLE9BQU87QUFBQSxJQUNILGFBQWE7QUFBQSxJQUNiLFFBQVE7QUFBQSxJQUNSLEtBQUs7QUFBQSxNQUNELE9BQU87QUFBQSxRQUNIO0FBQUEsUUFDQTtBQUFBLFFBQ0E7QUFBQSxNQUNKO0FBQUEsTUFDQSxTQUFTLENBQUMsSUFBSTtBQUFBLElBQ2xCO0FBQUEsSUFDQSxRQUFRO0FBQUEsSUFDUixlQUFlO0FBQUEsTUFDWCxVQUFVLENBQUMsc0JBQXNCO0FBQUEsSUFDckM7QUFBQSxFQUNKO0FBQUEsRUFDQSxTQUFTO0FBQUEsSUFDTCxPQUFPO0FBQUEsTUFDSCxnQkFBZ0I7QUFBQSxJQUNwQjtBQUFBLEVBQ0o7QUFDSixDQUFDOyIsCiAgIm5hbWVzIjogW10KfQo=
