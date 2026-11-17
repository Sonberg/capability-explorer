import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  base: "/process-landscape/",
  plugins: [react()],
  build: { outDir: "dist", emptyOutDir: true },
  server: {
    port: 5174,
    strictPort: true,
    proxy: { "/api": "http://127.0.0.1:5080" },
  },
});
