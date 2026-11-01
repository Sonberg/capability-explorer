/** Safe projections of configuration literals. Never return an unparsed connection string. */
function dnsHost(host: string): boolean {
  return host.length <= 253 && host.split('.').every(label => /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/i.test(label));
}
export function serviceBusNamespace(value: string): string | undefined {
  if (value.length > 16384 || /[\r\n\0]/.test(value)) return undefined;
  let endpoint = value.trim();
  if (endpoint.includes(';') || endpoint.includes('=')) {
    const fields = endpoint.split(';').filter(field => field.trim().length > 0);
    const endpoints: string[] = [];
    for (const field of fields) {
      const at = field.indexOf('=');
      if (at < 1) return undefined;
      const name = field.slice(0,at).trim().toLowerCase();
      if (!['endpoint','sharedaccesskeyname','sharedaccesskey','sharedaccesssignature','entitypath'].includes(name)) return undefined;
      if (name === 'endpoint') endpoints.push(field.slice(at+1).trim());
    }
    if (endpoints.length !== 1 || !/^sb:\/\//i.test(endpoints[0]!)) return undefined;
    endpoint = endpoints[0]!;
  }
  if (/^sb:\/\//i.test(endpoint)) {
    try {
      const url = new URL(endpoint);
      if (url.username || url.password || url.port || url.search || url.hash || !['','/'].includes(url.pathname)) return undefined;
      return dnsHost(url.hostname) ? url.hostname.toLowerCase() : undefined;
    } catch { return undefined; }
  }
  // A bare fully qualified namespace is data, never a fallback for malformed URI/connection-string input.
  return dnsHost(endpoint) && endpoint.includes('.') ? endpoint.toLowerCase() : undefined;
}
export function postgresStore(value: string): string | undefined {
  if (value.length > 16384 || /[\r\n\0]/.test(value)) return undefined;
  try {
    const url = new URL(value);
    if (!['postgres:','postgresql:'].includes(url.protocol) || !dnsHost(url.hostname)) return undefined;
    const originalPath = /^[a-z]+:\/\/[^/?#]*([^?#]*)/i.exec(value)?.[1];
    // Inspect the raw path too: URL normalization must not hide nested or dot-segment ambiguity.
    if (!originalPath || originalPath !== url.pathname || !/^\/(?:[A-Za-z0-9_.~-]|%[0-9A-Fa-f]{2})+$/.test(originalPath)) return undefined;
    const database = originalPath.slice(1);
    if (database === '.' || database === '..' || database.length > 512) return undefined;
    // Do not decode (%2F is part of one encoded database name), and never include URL credentials/search/hash.
    return `${url.hostname.toLowerCase()}/${database}`;
  } catch { return undefined; }
}
