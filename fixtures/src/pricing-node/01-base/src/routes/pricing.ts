import { Router } from "express";
import { getPrice } from "../handlers/getPrice.js";
import { recalculate } from "../handlers/recalculate.js";

const router = Router();
router.get("/:articleId", getPrice);
router.post("/:articleId/recalculate", recalculate);

export { router as pricingRouter };
