import { build } from "vite";

export async function buildModelFrame(outDir: string) {
  await build({
    configFile: false,
    root: new URL(".", import.meta.url).pathname,
    publicDir: false,
    build: {
      outDir,
      emptyOutDir: false,
      sourcemap: false,
      cssCodeSplit: false,
      rollupOptions: {
        input: new URL("src/models/viewer-frame-entry.ts", import.meta.url).pathname,
        output: {
          format: "iife",
          name: "Issue78ModelViewer",
          entryFileNames: "assets/model-viewer-frame.js",
          assetFileNames: "assets/model-viewer-frame[extname]",
        },
      },
    },
    logLevel: "warn",
  });
}
