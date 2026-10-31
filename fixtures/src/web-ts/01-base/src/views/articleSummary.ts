import { fetchWeeklySales } from "../api/salesClient";
import { averagePerActiveWeek, type DraftSale } from "../sales/weeklyStats";

function formatQuantity(value: number): string {
  return value.toFixed(2);
}

export async function renderArticleSummary(
  element: HTMLElement,
  articleId: string,
  from: string,
  to: string,
  drafts: readonly DraftSale[],
): Promise<void> {
  const server = await fetchWeeklySales(articleId, from, to);
  const draftAverage = averagePerActiveWeek(drafts);
  element.textContent = `${formatQuantity(server.averagePerWeek)} / week (drafts: ${formatQuantity(draftAverage)})`;
}
