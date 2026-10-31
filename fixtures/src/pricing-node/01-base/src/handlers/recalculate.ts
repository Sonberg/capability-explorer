import type { Request, Response } from "express";
import { SalesApi } from "../generated/salesApi.js";
import { saveSignal } from "../db/priceRepository.js";

const salesApi = new SalesApi(process.env.SALES_API_URL ?? "http://sales-api");

export async function recalculate(req: Request<{ articleId: string }>, res: Response): Promise<void> {
  const to = new Date().toISOString().slice(0, 10);
  const from = new Date(Date.now() - 28 * 86_400_000).toISOString().slice(0, 10);
  const sales = await salesApi.getWeeklySales(req.params.articleId, from, to);
  await saveSignal(req.params.articleId, sales.averagePerWeek);
  res.status(202).end();
}
