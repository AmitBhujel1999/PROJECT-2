using Accounting.Core.CashBank;
using Accounting.Core.Data;
using Accounting.Core.Expenses;
using Accounting.Core.Parties;
using Accounting.Core.Payments;
using Accounting.Core.Products;
using Accounting.Core.Security;
using Accounting.Core.Trade;

namespace Accounting.Core.Common;

/// <summary>
/// Optional sample data to try the program with. Every name says "(Demo)" so it is never mistaken
/// for real records. Only runs on a company file without products or parties.
/// </summary>
public static class DemoData
{
    public static void Seed(Database db, Actor admin)
    {
        db.Write(tx =>
        {
            if (tx.Scalar<long>("SELECT (SELECT COUNT(*) FROM products) + (SELECT COUNT(*) FROM parties)") > 0)
                throw new BusinessError("Demo data can only be added to an empty company.", "NOT_EMPTY");
            var today = tx.Today;
            var start = today.AddDays(-60);

            var bank = MoneyAccounts.Create(tx, admin, new MoneyAccount { Name = "Demo Bank Ltd. (Demo)", Kind = AccountKind.Bank, AccountNumber = "0012345678", OpeningBalance = 250000 });
            var cash = MoneyAccounts.Create(tx, admin, new MoneyAccount { Name = "Cash in Hand", Kind = AccountKind.Cash, OpeningBalance = 15000 });

            Product P(string name, string sku, Unit unit, decimal cost, decimal price, decimal stock, decimal reorder, decimal tax = 13) =>
                ProductService.Create(tx, admin, new ProductInput
                {
                    Name = name + " (Demo)", SkuCode = sku, Unit = unit, PurchasePrice = cost, SellingPrice = price,
                    OpeningStock = stock, ReorderLevel = reorder, TaxRate = tax,
                }, start);
            var rice = P("Basmati Rice 25kg", "DEMO-RICE25", Unit.Pcs, 2800, 3300, 40, 10);
            var oil = P("Sunflower Oil 1L", "DEMO-OIL1L", Unit.Ltr, 210, 260, 120, 30);
            var sugar = P("Sugar", "DEMO-SUGAR", Unit.Kg, 95, 115, 300, 50);
            var tea = P("Ilam Tea 500g", "DEMO-TEA500", Unit.Pack, 320, 420, 25, 20);
            var soap = P("Bath Soap (box of 12)", "DEMO-SOAP12", Unit.Box, 540, 660, 8, 10);
            var lentil = P("Red Lentils", "DEMO-LENTIL", Unit.Kg, 140, 175, 0, 25, tax: 0);

            Party C(PartyType type, string name, CreditTerms terms, string phone, string pan = "") =>
                PartyService.Create(tx, admin, type, new PartyInput { Name = name + " (Demo)", CreditTerms = terms, Phone = phone, PanVatNo = pan, CreditLimit = type == PartyType.Customer ? 200000 : 0 });
            var shyam = C(PartyType.Customer, "Shyam Kirana Pasal", CreditTerms.Days30, "9801000001", "601234567");
            var everest = C(PartyType.Customer, "Everest Mart", CreditTerms.Days15, "9801000002", "601234568");
            var walkIn = C(PartyType.Customer, "Walk-in Customer", CreditTerms.Cash, "");
            var himal = C(PartyType.Vendor, "Himal Traders", CreditTerms.Days30, "9851000001", "301112223");
            var valley = C(PartyType.Vendor, "Valley Distributors", CreditTerms.Days45, "9851000002", "301112224");

            DocumentInput Doc(Party party, int daysAgo, PaymentInput? payment, params LineItemInput[] items) =>
                new() { PartyId = party.Id, Date = today.AddDays(-daysAgo), Items = items, Payment = payment };

            TradeService.Create(tx, admin, TradeKind.Purchase, Doc(himal, 50, null, new(rice.Id, 20), new(sugar.Id, 200)) with { VendorBillNumber = "HT-1021" });
            TradeService.Create(tx, admin, TradeKind.Purchase, Doc(valley, 35, new PaymentInput { PayInFull = true, AccountId = bank.Id }, new(oil.Id, 60), new(tea.Id, 20), new(lentil.Id, 80)) with { VendorBillNumber = "VD-554" });
            TradeService.Create(tx, admin, TradeKind.Sale, Doc(shyam, 45, null, new(rice.Id, 10), new(oil.Id, 24, null, DiscountType.Percentage, 5)));
            TradeService.Create(tx, admin, TradeKind.Sale, Doc(everest, 28, null, new(sugar.Id, 120), new(tea.Id, 10)) with { DiscountType = DiscountType.Fixed, DiscountValue = 500 });
            TradeService.Create(tx, admin, TradeKind.Sale, Doc(walkIn, 10, new PaymentInput { PayInFull = true, AccountId = cash.Id }, new(soap.Id, 2), new(lentil.Id, 5)));
            TradeService.Create(tx, admin, TradeKind.Sale, Doc(shyam, 3, null, new(rice.Id, 6), new(lentil.Id, 40)));
            TradeService.Create(tx, admin, TradeKind.Sale, Doc(walkIn, 0, new PaymentInput { PayInFull = true, AccountId = cash.Id }, new(oil.Id, 6), new(sugar.Id, 10)));

            PartyAccounts.CreatePayment(tx, admin, AccountConfig.Receivable, shyam.Id, today.AddDays(-20), 30000, accountId: bank.Id, referenceNumber: "CHQ-7781", autoAllocate: true);
            PartyAccounts.CreatePayment(tx, admin, AccountConfig.Payable, himal.Id, today.AddDays(-15), 50000, accountId: bank.Id, autoAllocate: true);

            var categories = ExpenseService.Categories(tx).ToDictionary(c => c.Name);
            ExpenseService.Create(tx, admin, new ExpenseInput { Date = today.AddDays(-30), CategoryId = categories["Rent"].Id, Description = "Shop rent (Demo)", Amount = 25000, AccountId = bank.Id });
            ExpenseService.Create(tx, admin, new ExpenseInput { Date = today.AddDays(-12), CategoryId = categories["Utilities"].Id, Description = "Electricity bill (Demo)", Amount = 3200, TaxRate = 13, AccountId = cash.Id });
            CashBook.CreateContra(tx, admin, today.AddDays(-5), cash.Id, bank.Id, 10000, notes: "Cash deposited (Demo)");
        });
    }
}
