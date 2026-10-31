CREATE TABLE dbo.ArticleStats
(
    ArticleId      nvarchar(50)   NOT NULL PRIMARY KEY,
    AveragePerWeek decimal(18, 2) NOT NULL,
    UpdatedAt      datetime2      NOT NULL
);
