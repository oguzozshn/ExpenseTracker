using ExpenseTracker.Services;

namespace ExpenseTracker.Tests;

/// <summary>
/// The parser is the one place where a wrong answer is silent and expensive:
/// reading "1.500" as 1,50 ₺ loses three digits of a real payment.
/// </summary>
public class MoneyTests
{
    [Theory]
    [InlineData("1234", 123400)]
    [InlineData("1234,56", 123456)]
    [InlineData("1234.56", 123456)]
    [InlineData("1.234,56", 123456)]
    [InlineData("1,234.56", 123456)]
    [InlineData("0,05", 5)]
    [InlineData("12,5", 1250)]
    [InlineData("12.5", 1250)]
    [InlineData(" 1 234,50 ₺ ", 123450)]
    public void Parses_the_shapes_people_type(string input, long expected)
    {
        Assert.True(Money.TryParse(input, out var minor));
        Assert.Equal(expected, minor);
    }

    [Theory]
    [InlineData("1.500", 150000)]
    [InlineData("18.500", 1850000)]
    [InlineData("1.234.567", 123456700)]
    public void Reads_a_three_digit_group_after_a_dot_as_thousands(string input, long expected)
    {
        Assert.True(Money.TryParse(input, out var minor));
        Assert.Equal(expected, minor);
    }

    [Theory]
    [InlineData("12,555", 1256)]
    [InlineData("12,554", 1255)]
    public void Rounds_rather_than_truncates_extra_decimals(string input, long expected)
    {
        Assert.True(Money.TryParse(input, out var minor));
        Assert.Equal(expected, minor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData(null)]
    public void Rejects_what_is_not_a_number(string? input)
    {
        Assert.False(Money.TryParse(input, out var minor));
        Assert.Equal(0, minor);
    }

    [Fact]
    public void Round_trips_through_the_input_form()
    {
        const long amount = 1850050;
        Assert.True(Money.TryParse(Money.ToInput(amount), out var parsed));
        Assert.Equal(amount, parsed);
    }

    [Fact]
    public void Formats_with_a_turkish_grouping()
    {
        Assert.Equal("1.234,56 ₺", Money.Format(123456, "₺"));
        Assert.Equal("−1.234,56 ₺", Money.FormatSigned(-123456, "₺"));
    }
}
