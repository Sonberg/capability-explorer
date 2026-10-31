# Forecast API (fixture)

Forecasts weekly demand per article. Calls the Sales API for the current
weekly average, consumes `article-stats-updated` and stores forecasts in
PostgreSQL.
