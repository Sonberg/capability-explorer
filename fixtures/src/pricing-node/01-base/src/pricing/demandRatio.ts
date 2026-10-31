import type { ForecastResponse } from "../clients/forecastClient.js";

export function demandRatio(forecastPerWeek: number, capacityPerWeek: number): number;
export function demandRatio(forecast: ForecastResponse, capacityPerWeek: number): number;
export function demandRatio(forecast: number | ForecastResponse, capacityPerWeek: number): number {
  const perWeek = typeof forecast === "number" ? forecast : forecast.forecastPerWeek;
  return capacityPerWeek === 0 ? Number.POSITIVE_INFINITY : perWeek / capacityPerWeek;
}
