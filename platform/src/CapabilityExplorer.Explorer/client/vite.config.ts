import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { buildModelFrame } from "./vite.model-frame.ts";
const clientUrl = new URL(".", import.meta.url);
let modelFrameOutDir = new URL("../wwwroot", clientUrl).pathname;
const codeSplitting = {
  minSize: 20_000,
  groups: [
    { name: "likec4-react", test: /node_modules[\\/]likec4[\\/]react[\\/]index\.mjs$/, maxSize: 400_000, priority: 40 },
    { name: "graphviz-wasm", test: /node_modules[\\/]@hpcc-js[\\/]wasm-graphviz[\\/]/, maxSize: 400_000, priority: 30 },
    { name: "architecture-vendor", test: /node_modules[\\/](?:@likec4|likec4|@xyflow|@dagrejs)[\\/]/, maxSize: 400_000, priority: 20 },
    { name: "vendor", test: /node_modules[\\/]/, maxSize: 400_000, priority: 1 },
  ],
};

// Built into the server's wwwroot; `npm run dev` proxies the API to a running explorer.
export default defineConfig({
  resolve: {
    dedupe: ["react", "react-dom", "@xyflow/react", "@likec4/core"],
    alias: [{
      find: "@hpcc-js/wasm-graphviz",
      replacement: new URL("node_modules/@hpcc-js/wasm-graphviz", clientUrl).pathname,
    }, {
      find: "@dagrejs/dagre",
      replacement: new URL("node_modules/@dagrejs/dagre", clientUrl).pathname,
    }, {
      find: "likec4",
      replacement: new URL("node_modules/likec4", clientUrl).pathname,
    }],
  },
  worker: {
    format: "es",
    rolldownOptions: {
      output: { codeSplitting },
    },
  },
  plugins: [react(), tailwindcss(), {
    name: "issue78-isolated-model-viewer",
    apply: "build",
    configResolved: (config) => { modelFrameOutDir = config.build.outDir; },
    closeBundle: () => buildModelFrame(modelFrameOutDir),
  }],
  build: {
    outDir: "../wwwroot",
    emptyOutDir: true,
    sourcemap: false,
    rollupOptions: {
      input: {
        app: new URL("index.html", clientUrl).pathname,
      },
      output: {
        entryFileNames: "assets/[name].js",
        chunkFileNames: "assets/[name].js",
        assetFileNames: "assets/[name][extname]",
        codeSplitting,
      },
    },
  },
  server: { port: 5173, proxy: { "/api": "http://127.0.0.1:5080" } },
});
