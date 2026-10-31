CREATE TABLE dbo.Sales
(
    Id        bigint IDENTITY(1, 1) NOT NULL PRIMARY KEY,
    ArticleId nvarchar(50)          NOT NULL,
    SoldOn    date                  NOT NULL,
    Quantity  decimal(18, 3)        NULL,
    IsReturn  bit                   NOT NULL DEFAULT 0
);
