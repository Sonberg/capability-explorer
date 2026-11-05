import { useEffect, useState } from "react";

type HealthResponse = {
  status: "healthy" | "degraded" | "unhealthy";
  uptime_seconds: number;
};

type HealthState = { status: HealthResponse["status"] | "checking" | "unavailable"; uptimeSeconds?: number };

export function HealthStatus() {
  const [health, setHealth] = useState<HealthState>({ status: "checking" });

  useEffect(() => {
    let active = true;
    const check = async () => {
      try {
        const response = await fetch("/health", { cache: "no-store", headers: { accept: "application/json" } });
        const body = await response.json() as HealthResponse;
        if (!["healthy", "degraded", "unhealthy"].includes(body.status) ||
            !Number.isFinite(body.uptime_seconds) || body.uptime_seconds < 0) {
          throw new Error("Invalid health response");
        }
        if (active) setHealth({ status: body.status, uptimeSeconds: body.uptime_seconds });
      } catch {
        if (active) setHealth({ status: "unavailable" });
      }
    };

    void check();
    const timer = window.setInterval(check, 10_000);
    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, []);

  const label = health.status === "checking" ? "Checking health"
    : health.status === "unavailable" ? "Unavailable"
    : `${health.status[0].toUpperCase()}${health.status.slice(1)} · ${Math.floor(health.uptimeSeconds ?? 0)}s uptime`;
  const tone = health.status === "healthy" ? "bg-emerald-500"
    : health.status === "degraded" ? "bg-amber-500"
    : health.status === "checking" ? "bg-zinc-400"
    : "bg-red-500";

  return (
    <span className="inline-flex shrink-0 items-center gap-1.5 rounded-md border border-zinc-200 px-2 py-1 text-xs text-zinc-600 dark:border-zinc-700 dark:text-zinc-300"
      role="status" aria-label={`Service health: ${label}`} title="Live process health">
      <span className={`size-2 rounded-full ${tone}`} aria-hidden="true" />
      <span className="hidden sm:inline">{label}</span>
      <span className="sm:hidden">{health.status === "healthy" ? "OK" : health.status === "checking" ? "…" : "!"}</span>
    </span>
  );
}
