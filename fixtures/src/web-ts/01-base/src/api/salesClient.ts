import { salesApiBaseUrl } from "../config";

export interface WeeklySalesResponse {
  articleId: string;
  from: string;
  to: string;
  averagePerWeek: number;
}

export async function fetchWeeklySales(articleId: string, from: string, to: string): Promise<WeeklySalesResponse> {
  const url = `${salesApiBaseUrl}/api/articles/${encodeURIComponent(articleId)}/weekly-sales?from=${from}&to=${to}`;
  const response = await fetch(url, { method: "GET" });
  if (!response.ok) {
    throw new Error(`weekly-sales failed: ${response.status}`);
  }
  return (await response.json()) as WeeklySalesResponse;
}
