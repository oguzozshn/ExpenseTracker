using ExpenseTracker.Services.Import;

namespace ExpenseTracker.Tests;

public class StatementValueTests
{
    [Theory]
    [InlineData("03.08.2026")]
    [InlineData("3.8.2026")]
    [InlineData("03/08/2026")]
    [InlineData("03-08-2026")]
    [InlineData("2026-08-03")]
    [InlineData("03.08.2026 14:22")]
    [InlineData("2026-08-03T00:00:00")]
    [InlineData("3 Ağustos 2026")]
    public void Reads_the_date_formats_banks_use(string input)
    {
        Assert.True(StatementValues.TryDate(input, out var date));
        Assert.Equal(new DateOnly(2026, 8, 3), date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Toplam")]
    [InlineData("--")]
    public void Rejects_a_row_without_a_date(string input) =>
        Assert.False(StatementValues.TryDate(input, out _));

    [Theory]
    [InlineData("1.234,56", 123456)]
    [InlineData("-1.234,56", -123456)]
    [InlineData("1.234,56-", -123456)]
    [InlineData("(1.234,56)", -123456)]
    [InlineData("+1.234,56", 123456)]
    [InlineData("1.234,56 TL", 123456)]
    [InlineData("-18.500,00 ₺", -1850000)]
    public void Reads_every_way_a_statement_writes_a_negative(string input, long expected)
    {
        Assert.True(StatementValues.TryAmount(input, out var minor));
        Assert.Equal(expected, minor);
    }

    [Fact]
    public void An_empty_amount_is_not_a_zero()
    {
        Assert.False(StatementValues.TryAmount("", out _));
        Assert.False(StatementValues.TryAmount("   ", out _));
    }

    [Theory]
    [InlineData("MİGROS", "MIGROS")]
    [InlineData("migros  ticaret", "MIGROS TICARET")]
    [InlineData("ŞOK  MARKET", "SOK MARKET")]
    [InlineData("Getir Yemek", "GETIR YEMEK")]
    [InlineData("  A101  ", "A101")]
    public void Folds_turkish_letters_so_one_merchant_is_one_string(string input, string expected) =>
        Assert.Equal(expected, StatementText.Normalize(input));

    [Fact]
    public void Matching_ignores_case_and_turkish_letters()
    {
        Assert.True(StatementText.Contains("MİGROS KADIKÖY", "migros"));
        Assert.True(StatementText.Contains("Şok Market Beşiktaş", "SOK MARKET"));
        Assert.False(StatementText.Contains("MIGROS", "CARREFOUR"));
        Assert.False(StatementText.Contains("MIGROS", ""));
    }
}
