import express from "express";
import { pricingRouter } from "./routes/pricing.js";
import { reportRouter } from "./routes/reports.js";
import { startStatsListener } from "./messaging/statsListener.js";

const app = express();
app.use(express.json());
app.use("/api/pricing", pricingRouter);
app.use("/api/pricing/reports", reportRouter);

await startStatsListener();
app.listen(Number(process.env.PORT ?? 8080));
