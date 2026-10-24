using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Accounting.Server;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Builder;

namespace Accounting.Tests;

/// <summary>The built-in server over real HTTP: the conventions the web screens and the phone app rely on.</summary>
public sealed class ServerTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "acct-server-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private readonly CookieContainer _cookies = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _app = ApiServer.Build(new ServerOptions(Path.Combine(_folder, "company.db"), null, ["http://127.0.0.1:0"]));
        await _app.StartAsync();
        _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies }) { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private async Task<JsonElement> Send(HttpMethod method, string path, object? body = null, HttpStatusCode expect = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = body as HttpContent ?? JsonContent.Create(body);
        if (_cookies.GetCookies(_http.BaseAddress!)[ApiServer.CsrfCookie] is { } csrf)
            request.Headers.Add("X-CSRFToken", csrf.Value);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expect, $"{method} {path}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private Task<JsonElement> Get(string path, HttpStatusCode expect = HttpStatusCode.OK) => Send(HttpMethod.Get, path, null, expect);
    private Task<JsonElement> Post(string path, object body, HttpStatusCode expect = HttpStatusCode.OK) => Send(HttpMethod.Post, path, body, expect);

    /// <summary>First-run setup on this computer, then signed in as the new admin.</summary>
    private async Task SetUp(bool demo = false)
    {
        await Get("/api/auth/csrf/");
        await Post("/api/companies/", new
        {
            name = "Himal Traders", setup = true, admin_username = "admin", admin_password = "Admin@12345", date_system = "AD", demo,
        }, HttpStatusCode.Created);
        await Post("/api/auth/login/", new { username = "admin", password = "Admin@12345" });
    }

    [Fact]
    public async Task FreshInstallAsksForSetupOnceThenSignsIn()
    {
        var companies = await Get("/api/companies/");
        Assert.True(companies.GetProperty("data").GetProperty("setup").GetBoolean());
        await SetUp();
        Assert.False((await Get("/api/companies/")).GetProperty("data").GetProperty("setup").GetBoolean());
        var again = await Post("/api/companies/", new { name = "X", setup = true, admin_username = "b", admin_password = "Admin@12345" }, HttpStatusCode.Conflict);
        Assert.Equal("ALREADY_SET_UP", again.GetProperty("error").GetProperty("code").GetString());
        var me = await Get("/api/auth/me/");
        Assert.Equal("admin", me.GetProperty("data").GetProperty("username").GetString());
        var settings = await Get("/api/settings/");
        Assert.Equal("Himal Traders", settings.GetProperty("data").GetProperty("business_name").GetString());
        Assert.Equal("AD", settings.GetProperty("data").GetProperty("date_system").GetString());
    }

    [Fact]
    public async Task SetupErrorsPointAtTheAdminFields()
    {
        await Get("/api/auth/csrf/");
        var bad = await Post("/api/companies/", new { name = "Himal", setup = true, admin_username = "admin", admin_password = "12345" },
            HttpStatusCode.BadRequest);
        Assert.True(bad.GetProperty("error").GetProperty("details").TryGetProperty("admin_password", out _));
        Assert.True((await Get("/api/companies/")).GetProperty("data").GetProperty("setup").GetBoolean());
    }

    [Fact]
    public async Task ApiNeedsSignInAndCsrfToken()
    {
        var anonymous = await Get("/api/products/", HttpStatusCode.Unauthorized);
        Assert.False(anonymous.GetProperty("success").GetBoolean());
        await SetUp();
        using var noToken = await _http.PostAsJsonAsync("/api/products/", new { name = "Rice", sku_code = "R1" });
        Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);
        Assert.Contains("CSRF_FAILED", await noToken.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ErrorsUseTheDjangoEnvelope()
    {
        await SetUp();
        var bad = await Post("/api/products/", new { name = "Rice", sku_code = "R1", selling_price = "-5" }, HttpStatusCode.BadRequest);
        var error = bad.GetProperty("error");
        Assert.Equal("VALIDATION_ERROR", error.GetProperty("code").GetString());
        Assert.Equal("Selling price: Ensure this value is greater than or equal to 0.", error.GetProperty("message").GetString());
        Assert.Equal("Ensure this value is greater than or equal to 0.", error.GetProperty("details").GetProperty("selling_price")[0].GetString());
        var missing = await Get("/api/products/999/", HttpStatusCode.NotFound);
        Assert.Equal("NOT_FOUND", missing.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ListsArePagedWithDecimalStrings()
    {
        await SetUp(demo: true);
        var list = (await Get("/api/products/?page_size=25")).GetProperty("data");
        Assert.Equal(1, list.GetProperty("page").GetInt32());
        Assert.True(list.GetProperty("count").GetInt32() > 0);
        var first = list.GetProperty("results")[0];
        Assert.Equal(JsonValueKind.String, first.GetProperty("selling_price").ValueKind);
        Assert.Matches(@"^\d+\.\d{2}$", first.GetProperty("selling_price").GetString());
        Assert.Matches(@"^\d+\.\d{3}$", first.GetProperty("current_stock").GetString());
    }

    [Fact]
    public async Task BulkSaveIsAllOrNothing()
    {
        await SetUp();
        var bad = await Post("/api/products/bulk/", new
        {
            rows = new object[]
            {
                new { name = "Rice", sku_code = "RICE" },
                new { name = "Rice again", sku_code = "rice" },
                new { name = "", sku_code = "X" },
            },
        }, HttpStatusCode.BadRequest);
        Assert.Equal("BULK_INVALID", bad.GetProperty("error").GetProperty("code").GetString());
        var rows = bad.GetProperty("error").GetProperty("details").GetProperty("rows");
        Assert.Equal("Used twice in this list.", rows.GetProperty("1").GetProperty("sku_code").GetString());
        Assert.True(rows.GetProperty("2").TryGetProperty("name", out _));
        Assert.Equal(0, (await Get("/api/products/")).GetProperty("data").GetProperty("count").GetInt32());

        var ok = await Post("/api/products/bulk/", new
        {
            rows = new object[] { new { name = "Rice", sku_code = "RICE", unit = "KG" }, new { name = "Tea", sku_code = "TEA", is_active = false } },
        });
        Assert.Equal("Saved: 2 new items added.", ok.GetProperty("message").GetString());
        var tea = (await Get("/api/products/?search=TEA")).GetProperty("data").GetProperty("results")[0];
        Assert.False(tea.GetProperty("is_active").GetBoolean());

        var id = tea.GetProperty("id").GetInt64();
        var same = await Post("/api/products/bulk/", new { rows = new object[] { new { id, name = "Tea" } } });
        Assert.Equal("Nothing changed.", same.GetProperty("message").GetString());
        var changed = await Post("/api/products/bulk/", new { rows = new object[] { new { id, selling_price = "450", is_active = true } } });
        Assert.Equal("Saved: 1 item changed.", changed.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ExcelFormatAndUploadRoundTrip()
    {
        await SetUp();
        using var template = await _http.GetAsync("/api/customers/bulk/template/");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", template.Content.Headers.ContentType?.MediaType);
        using var book = new XLWorkbook(await template.Content.ReadAsStreamAsync());
        var sheet = book.Worksheet("Customers");
        Assert.Equal("Name", sheet.Cell(1, 1).GetString());
        Assert.Equal("Max credit (0 = no limit)", sheet.Cell(1, 8).GetString());
        sheet.Cell(4, 1).Value = "Ram Traders";
        sheet.Cell(4, 2).Value = 9800000003;
        sheet.Cell(4, 6).Value = "30 Days";

        using var buffer = new MemoryStream();
        book.SaveAs(buffer);
        var form = new MultipartFormDataContent { { new ByteArrayContent(buffer.ToArray()), "file", "customers.xlsx" } };
        var upload = (await Post("/api/customers/bulk/upload/", form)).GetProperty("data");
        Assert.Equal(2, upload.GetProperty("skipped_examples").GetInt32());
        var row = Assert.Single(upload.GetProperty("rows").EnumerateArray());
        Assert.Equal("Ram Traders", row.GetProperty("name").GetString());
        Assert.Equal("9800000003", row.GetProperty("phone").GetString());
        Assert.Equal("30 Days", row.GetProperty("credit_terms").GetString());

        var csv = new MultipartFormDataContent { { new StringContent("Name,Phone\n\"Shrestha, Sons\",98\n"), "file", "c.csv" } };
        var fromCsv = (await Post("/api/customers/bulk/upload/", csv)).GetProperty("data").GetProperty("rows")[0];
        Assert.Equal("Shrestha, Sons", fromCsv.GetProperty("name").GetString());

        var noHeadings = new MultipartFormDataContent { { new StringContent("a,b\n1,2\n"), "file", "c.csv" } };
        var error = await Post("/api/customers/bulk/upload/", noHeadings, HttpStatusCode.BadRequest);
        Assert.Equal("NO_HEADINGS", error.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SaleCalculatesVatAndServesPdf()
    {
        await SetUp();
        var product = (await Post("/api/products/", new { name = "Rice", sku_code = "R1", unit = "KG", opening_stock = "10", purchase_price = "100", selling_price = "150" },
            HttpStatusCode.Created)).GetProperty("data").GetProperty("id").GetInt64();
        var customer = (await Post("/api/customers/", new { name = "Sita" }, HttpStatusCode.Created)).GetProperty("data").GetProperty("id").GetInt64();
        var sale = (await Post("/api/sales/", new
        {
            party = customer, date = DateTime.Today.ToString("yyyy-MM-dd"),
            items = new[] { new { product, quantity = "2", unit_price = "150", tax_rate = "13" } },
        }, HttpStatusCode.Created)).GetProperty("data");
        Assert.Equal("39.00", sale.GetProperty("tax_amount").GetString());
        Assert.Equal("339.00", sale.GetProperty("total_amount").GetString());
        using var pdf = await _http.GetAsync($"/api/sales/{sale.GetProperty("id").GetInt64()}/pdf/?inline=1");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));
    }
}
