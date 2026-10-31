CREATE FUNCTION dbo.fnWeeklySalesAverage
(
    @ArticleId nvarchar(50),
    @From      date,
    @To        date
)
RETURNS TABLE
AS
RETURN
    SELECT
        ROUND(
            SUM(ISNULL(s.Quantity, 0))
            / NULLIF(DATEDIFF(day, @From, @To) / 7 + 1, 0),
            2) AS AveragePerWeek
    FROM dbo.Sales AS s
    WHERE s.ArticleId = @ArticleId
      AND s.SoldOn BETWEEN @From AND @To;
