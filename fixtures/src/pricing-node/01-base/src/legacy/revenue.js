// @ts-check

/**
 * Weekly revenue in SEK for the legacy report.
 * @param {{ soldAt: string, amountOre: number, quantity: number, currency: string }[]} rows
 * @param {Record<string, number>} sekPerUnit
 * @param {number} weeks
 * @returns {number}
 */
export function weeklyRevenueSek(rows, sekPerUnit, weeks) {
  let totalOre = 0;
  for (const row of rows) {
    const rate = sekPerUnit[row.currency];
    if (rate === undefined) continue;
    totalOre += row.amountOre * row.quantity * rate;
  }
  return weeks > 0 ? Number((totalOre / 100 / weeks).toFixed(2)) : 0;
}
