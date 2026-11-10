import type { ViteDevServer } from "../platform/src/CapabilityExplorer.Explorer/client/node_modules/vite/dist/node/index.js";

/** Vite probes and releases port 0 before async initialization. Let the actual HTTP listener choose its port atomically. */
export function listenBrowserServer(vite: ViteDevServer): Promise<string> {
  const server = vite.httpServer;
  if (!server) throw new Error("The browser fixture requires a standalone HTTP server.");
  return new Promise((resolve, reject) => {
    const onError = (error: Error) => {
      server.off("listening", onListening);
      reject(error);
    };
    const onListening = () => {
      server.off("error", onError);
      const address = server.address();
      if (!address || typeof address === "string") {
        reject(new Error("The browser fixture did not bind a TCP address."));
        return;
      }
      resolve(`http://127.0.0.1:${address.port}`);
    };
    server.once("error", onError);
    server.once("listening", onListening);
    server.listen(0, "127.0.0.1");
  });
}
