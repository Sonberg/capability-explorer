import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// Built into the server's wwwroot; `npm run dev` proxies the API to a running explorer.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: { outDir: "../wwwroot", emptyOutDir: true, sourcemap: false },
  server: { port: 5173, proxy: { "/api": "http://127.0.0.1:5080" } },
});
