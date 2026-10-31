declare global {
  // Injected by the hosting page at runtime.
  var SALES_API_URL: string | undefined;
}

export const salesApiBaseUrl: string = globalThis.SALES_API_URL ?? "/sales-api";
