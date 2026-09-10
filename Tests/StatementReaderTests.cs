using System.Text;
using ExpenseTracker.Services.Import;

namespace ExpenseTracker.Tests;

public class StatementReaderTests
{
    [Fact]
    public void Splits_quoted_fields_without_losing_the_delimiter_inside()
    {
        var fields = StatementReader.SplitLine("03.08.2026;\"MIGROS; KADIKOY\";-125,40", ';');
        Assert.Equal(["03.08.2026", "MIGROS; KADIKOY", "-125,40"], fields);
    }

    [Fact]
    public void A_doubled_quote_is_a_literal_quote()
    {
        var fields = StatementReader.SplitLine("\"A \"\"B\"\" C\";1", ';');
        Assert.Equal(["A \"B\" C", "1"], fields);
    }

    [Fact]
    public void Picks_the_delimiter_that_is_consistent_across_the_file()
    {
        // Commas appear inside the descriptions; the real separator is the semicolon.
        var lines = new[]
        {
            "Tarih;Açıklama;Tutar",
            "03.08.2026;MIGROS, KADIKOY;-125,40",
            "04.08.2026;SHELL, ATASEHIR;-1.200,00"
        };

        Assert.Equal(';', StatementReader.DetectDelimiter(lines));
    }

    [Fact]
    public void Skips_the_letterhead_and_finds_the_real_header()
    {
        var text = string.Join('\n',
            "GARANTİ BBVA",
            "Hesap No: 1234-5678",
            "Dönem: 01.08.2026 - 31.08.2026",
            "",
            "Tarih;Açıklama;Tutar;Bakiye",
            "03.08.2026;MIGROS KADIKOY;-125,40;4.874,60",
            "04.08.2026;MAAS ODEMESI;25.000,00;29.874,60");

        var table = StatementReader.ReadDelimited(text);

        Assert.Equal(["Tarih", "Açıklama", "Tutar", "Bakiye"], table.Columns);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("MIGROS KADIKOY", table.Cell(table.Rows[0], 1));
    }

    [Fact]
    public void Reads_windows_1254_without_mangling_turkish_letters()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var text = "Tarih;Açıklama;Tutar\n03.08.2026;ŞOK MARKET ÜSKÜDAR;-99,90";
        var bytes = Encoding.GetEncoding(1254).GetBytes(text);

        var table = StatementReader.ReadDelimited(StatementReader.DecodeText(bytes));

        Assert.Equal("ŞOK MARKET ÜSKÜDAR", table.Cell(table.Rows[0], 1));
    }

    [Fact]
    public void Utf8_with_a_bom_still_reads_cleanly()
    {
        var bytes = new UTF8Encoding(true).GetBytes("Tarih;Açıklama;Tutar\n03.08.2026;MİGROS;-1,00");
        var table = StatementReader.ReadDelimited(StatementReader.DecodeText(bytes));

        Assert.Equal("Tarih", table.Columns[0]);
        Assert.Equal("MİGROS", table.Cell(table.Rows[0], 1));
    }

    [Fact]
    public void Guesses_the_columns_of_a_single_amount_statement()
    {
        var map = ColumnMap.Guess(["İşlem Tarihi", "Valör", "Açıklama", "Tutar", "Bakiye"]);

        Assert.Equal(0, map.Date);
        Assert.Equal(2, map.Description);
        Assert.Equal(3, map.Amount);
        Assert.False(map.UsesDebitCredit);
        Assert.True(map.IsComplete);
    }

    [Fact]
    public void Guesses_the_columns_of_a_debit_credit_statement()
    {
        var map = ColumnMap.Guess(["Tarih", "Açıklama", "Borç", "Alacak", "Bakiye"]);

        Assert.Equal(0, map.Date);
        Assert.Equal(1, map.Description);
        Assert.Equal(2, map.Debit);
        Assert.Equal(3, map.Credit);
        Assert.True(map.UsesDebitCredit);
        Assert.True(map.IsComplete);
    }

    [Fact]
    public void Never_mistakes_the_balance_column_for_the_amount()
    {
        var map = ColumnMap.Guess(["Tarih", "Açıklama", "Bakiye Tutarı"]);
        Assert.Equal(-1, map.Amount);
        Assert.False(map.IsComplete);
    }
}
