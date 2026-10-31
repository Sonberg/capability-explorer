import { Router, type Request, type Response } from "express";
import { listPrices } from "../db/priceRepository.js";

export const reportRouter = Router();

reportRouter.get("/prices", async (req: Request, res: Response) => {
  const sort = req.query.sort === "amount" ? "amount_ore" : "article_id";
  res.json(await listPrices(sort));
});
