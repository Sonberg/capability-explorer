using Sales.Shared.Money;

namespace Sales.Worker.Calculations;

public static class CurrencyNormalizer
{
    public static decimal ToSek(decimal amount, string currency) => ExchangeRates.Convert(amount, currency, "SEK");
}
