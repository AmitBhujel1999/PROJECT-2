using Accounting.Core;

namespace Accounting.Tests;

// Ported from backend/tests/test_calculations.py; every expected value is unchanged.
public class CalculationTests
{
    private const DiscountType Pct = DiscountType.Percentage;
    private const DiscountType Fix = DiscountType.Fixed;

    private static DocumentTotals Doc(LineInput[] lines, DiscountType? type = null, decimal value = 0m) =>
        Calculations.CalculateDocument(lines, type, value);

    [Fact]
    public void SpecPurchaseExamplePercentageItemDiscount()
    {
        // Qty 10 x 1,000, 10% discount, 13% VAT -> 10,170
        var t = Doc([new(10, 1000, 13, Pct, 10)]);
        var ln = t.Lines[0];
        Assert.Equal(10000.00m, ln.GrossAmount);
        Assert.Equal(1000.00m, ln.DiscountAmount);
        Assert.Equal(9000.00m, ln.TaxableAmount);
        Assert.Equal(1170.00m, ln.TaxAmount);
        Assert.Equal(10170.00m, ln.Total);
        Assert.Equal(10170.00m, t.TotalAmount);
    }

    [Fact]
    public void SpecSaleExample()
    {
        var t = Doc([new(5, 2000, 13, Pct, 10)]);
        Assert.Equal((9000.00m, 1170.00m, 10170.00m), (t.TaxableAmount, t.TaxAmount, t.TotalAmount));
    }

    [Fact]
    public void FixedItemDiscount()
    {
        var t = Doc([new(2, 750, 13, Fix, 100)]);
        Assert.Equal(100.00m, t.Lines[0].DiscountAmount);
        Assert.Equal(1400.00m, t.TaxableAmount);
        Assert.Equal(182.00m, t.TaxAmount);
        Assert.Equal(1582.00m, t.TotalAmount);
    }

    [Fact]
    public void InvoicePercentageDiscountAppliedAfterItemDiscount()
    {
        var t = Doc([new(5, 500, 13, Pct, 10), new(2, 750, 13)], Pct, 10);
        Assert.Equal(4000.00m, t.Subtotal);
        Assert.Equal(250.00m, t.ItemDiscountTotal);
        Assert.Equal(3750.00m, t.NetSubtotal);
        Assert.Equal(375.00m, t.DiscountAmount);
        Assert.Equal(625.00m, t.TotalDiscount);
        Assert.Equal(3375.00m, t.TaxableAmount);
        Assert.Equal(438.75m, t.TaxAmount);
        Assert.Equal(3813.75m, t.TotalAmount);
    }

    [Fact]
    public void InvoiceFixedDiscountAllocatedProRataAndSumsExactly()
    {
        var t = Doc([new(1, 100, 13), new(1, 100, 13), new(1, 100, 13)], Fix, 100);
        Assert.Equal(100.00m, t.Lines.Sum(l => l.InvoiceDiscountShare));
        Assert.Equal(200.00m, t.TaxableAmount);
        Assert.Equal(t.TaxableAmount, t.Lines.Sum(l => l.TaxableAmount));
    }

    [Fact]
    public void MixedTaxRatesComputedPerLine()
    {
        var t = Doc([new(1, 1000, 13), new(1, 1000, 0)], Fix, 200);
        Assert.Equal(900.00m, t.Lines[0].TaxableAmount);
        Assert.Equal(117.00m, t.Lines[0].TaxAmount);
        Assert.Equal(0.00m, t.Lines[1].TaxAmount);
        Assert.Equal(1917.00m, t.TotalAmount);
    }

    [Fact]
    public void RoundingHalfUp()
    {
        // 99.99 * 13% = 12.9987 -> 13.00
        var t = Doc([new(3, 33.33m, 13)]);
        Assert.Equal(13.00m, t.TaxAmount);
        Assert.Equal(112.99m, t.TotalAmount);
    }

    [Fact]
    public void FractionalQuantities()
    {
        var t = Doc([new(2.5m, 140, 0)]);
        Assert.Equal(350.00m, t.Subtotal);
    }

    public static TheoryData<LineInput, DiscountType?, decimal, string> InvalidInputs => new()
    {
        { new(1, 100, 13, Fix, 150), null, 0, "INVALID_DISCOUNT" },
        { new(1, 100, 13, Pct, 101), null, 0, "INVALID_DISCOUNT" },
        { new(1, 100, 13, Pct, -1), null, 0, "INVALID_DISCOUNT" },
        { new(1, 100, 13), Fix, 101, "INVALID_DISCOUNT" },
        { new(0, 100, 13), null, 0, "INVALID_QUANTITY" },
        { new(1, -1, 13), null, 0, "INVALID_PRICE" },
        { new(1, 1, 101), null, 0, "INVALID_TAX_RATE" },
    };

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void InvalidInputsRejected(LineInput line, DiscountType? type, decimal value, string code)
    {
        var ex = Assert.Throws<CalculationError>(() => Doc([line], type, value));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void NoItemsRejected() => Assert.Throws<CalculationError>(() => Doc([]));

    [Fact]
    public void FullDiscountGivesZeroNotNegative()
    {
        var t = Doc([new(1, 100, 13, Pct, 100)]);
        Assert.Equal(0.00m, t.TaxableAmount);
        Assert.Equal(0.00m, t.TotalAmount);
    }

    [Fact]
    public void ComputeDiscountNone() => Assert.Equal(0.00m, Calculations.ComputeDiscount(100, null, 50));
}
