import type { Request, Response } from "express";
import config from "../../config/default.json" with { type: "json" };
import { getForecast } from "../clients/forecastClient.js";
import { findPrice } from "../db/priceRepository.js";
import { demandRatio } from "../pricing/demandRatio.js";
import { priceForDemand } from "../pricing/priceForDemand.js";

export async function getPrice(req: Request<{ articleId: string }>, res: Response): Promise<void> {
  const articleId = Number.parseInt(req.params.articleId, 10);
  const [price, forecast] = await Promise.all([findPrice(req.params.articleId), getForecast(articleId)]);
  if (!price) {
    res.status(404).end();
    return;
  }
  const ratio = demandRatio(forecast, config.capacityPerWeek);
  res.json({ articleId, amountOre: priceForDemand(price.amount_ore, ratio), currency: price.currency });
}
