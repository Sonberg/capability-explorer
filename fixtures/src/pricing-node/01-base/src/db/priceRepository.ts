import pg from "pg";

export interface PriceRow {
  article_id: string;
  amount_ore: number;
  currency: string;
}

const pool = new pg.Pool({ connectionString: process.env.PRICING_DATABASE_URL });

const SELECT_PRICE = "SELECT article_id, amount_ore, currency FROM pricing.prices WHERE article_id = $1";
const INSERT_SIGNAL = "INSERT INTO pricing.demand_signals (article_id, average_per_week, received_at) VALUES ($1, $2, now())";

export async function findPrice(articleId: string): Promise<PriceRow | undefined> {
  const result = await pool.query<PriceRow>(SELECT_PRICE, [articleId]);
  return result.rows[0];
}

export async function listPrices(sortColumn: "article_id" | "amount_ore"): Promise<PriceRow[]> {
  const result = await pool.query<PriceRow>(`SELECT article_id, amount_ore, currency FROM pricing.prices ORDER BY ${sortColumn}`);
  return result.rows;
}

export async function saveSignal(articleId: string, averagePerWeek: number): Promise<void> {
  await pool.query(INSERT_SIGNAL, [articleId, averagePerWeek]);
}
