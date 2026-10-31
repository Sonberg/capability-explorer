import axios from "axios";

export interface ForecastResponse {
  articleId: number;
  strategy: string;
  forecastPerWeek: number;
  currentAveragePerWeek: number;
}

const http = axios.create({ baseURL: process.env.FORECAST_API_URL, timeout: 5_000 });

export async function getForecast(articleId: number, strategy = "exp-smoothing"): Promise<ForecastResponse> {
  const response = await http.get<ForecastResponse>(`/api/forecasts/${articleId}`, { params: { strategy } });
  return response.data;
}
