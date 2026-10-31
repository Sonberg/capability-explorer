import { isoWeekKey } from "./isoWeek";

export interface DraftSale {
  soldOn: string;
  quantity: number | null;
}

export function averagePerActiveWeek(rows: readonly DraftSale[]): number {
  const counted = rows.filter((r) => r.quantity !== null);
  const weeks = new Set(counted.map((r) => isoWeekKey(r.soldOn)));
  if (weeks.size === 0) {
    return 0;
  }
  const total = counted.reduce((sum, r) => sum + (r.quantity as number), 0);
  return Math.round((total / weeks.size) * 100) / 100;
}
