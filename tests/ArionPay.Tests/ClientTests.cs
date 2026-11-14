using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArionPay.Tests;

/// <summary>Answers each request with the next answer; an Exception answer is thrown (a network error).</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    internal sealed record Call(string Method, string Url, Dictionary<string, string> Headers, byte[]? Body);

    private readonly Queue<object> _answers;
    public readonly List<Call> Calls = new();

    public FakeHandler(params object[] answers) => _answers = new Queue<object>(answers);

    public static object Answer(int status, string json, string? retryAfter = null) => (status, json, retryAfter);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var h = request.Headers.ToDictionary(x => x.Key, x => string.Join(",", x.Value));
        if (request.Content is not null)
            foreach (var x in request.Content.Headers) h[x.Key] = string.Join(",", x.Value);
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
        Calls.Add(new Call(request.Method.Method, request.RequestUri!.ToString(), h, body));
        var a = _answers.Dequeue();
        if (a is Exception e) throw e;
        var (status, json, retryAfter) = ((int, string, string?))a;
        var res = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (retryAfter is not null) res.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return res;
    }
}

public class ClientTests
{
    private readonly List<TimeSpan> _waits = new();

    private ArionPayClient Client(FakeHandler f, string baseUrl = "https://example.test/", int maxAttempts = 3)
    {
        var c = new ArionPayClient(new ArionPayOptions { ApiKey = "pk_unit_000001", ApiSecret = "sk_unit", BaseUrl = baseUrl, MaxAttempts = maxAttempts }, new HttpClient(f));
        c.Delay = (d, _) => { _waits.Add(d); return Task.CompletedTask; };
        var ts = 1700000000L;
        c.Clock = () => ++ts;
        return c;
    }

    private static string Sig(string secret, string ts, byte[] body)
    {
        var p = Encoding.UTF8.GetBytes(ts + ".");
        return ArionPayWebhook.HmacHex(secret, p.Concat(body).ToArray());
    }

    [Fact]
    public void ConstructorChecks()
    {
        Assert.Contains("pk_", Assert.Throws<ArgumentException>(() => new ArionPayClient(new ArionPayOptions { ApiKey = "nope", ApiSecret = "s" })).Message);
        Assert.Contains("ApiSecret", Assert.Throws<ArgumentException>(() => new ArionPayClient(new ArionPayOptions { ApiKey = "pk_abcdefgh" })).Message);
        Assert.Contains("http", Assert.Throws<ArgumentException>(() => new ArionPayClient(new ArionPayOptions { ApiKey = "pk_abcdefgh", ApiSecret = "s", BaseUrl = "ftp://x" })).Message);
        Assert.Contains("http", Assert.Throws<ArgumentException>(() => new ArionPayClient(new ArionPayOptions { ApiKey = "pk_abcdefgh", ApiSecret = "s", BaseUrl = "not a url" })).Message);
        using var c = new ArionPayClient(new ArionPayOptions { ApiKey = "pk_abcdefgh", ApiSecret = "sk_hidden", AppInfo = "myshop/2.1" });
        Assert.Matches($"^arionpay-dotnet/{ArionPayClient.Version} dotnet/\\S+ myshop/2\\.1$", c.UserAgent);
        Assert.DoesNotContain("sk_hidden", c.ToString());
    }

    [Fact]
    public async Task CreateSignsExactBody()
    {
        var a24 = new string('a', 24);
        var f = new FakeHandler(FakeHandler.Answer(201, "{\"status\":\"success\",\"id\":\"" + a24 + "\",\"invoice_url\":\"https://x/pay/1\",\"data\":{\"id\":\"" + a24
            + "\",\"invoice_url\":\"https://x/pay/1\",\"amount_fiat\":9.5,\"currency\":\"EUR\",\"status\":\"new\",\"order_id\":\"7\",\"amount_crypto\":\"10.123456789012345678\"}}"));
        var inv = await Client(f).CreateInvoiceAsync(new InvoiceRequest
        {
            Amount = 9.50m, Currency = "EUR", OrderId = "7", Test = true,
            Metadata = new Dictionary<string, object?> { ["name"] = "Zoë <b>", ["n"] = 3 },
        });
        Assert.Equal("https://x/pay/1", inv.InvoiceUrl);
        Assert.Equal(9.5m, inv.AmountFiat);
        Assert.Equal("10.123456789012345678", inv.AmountCrypto);
        Assert.Equal("7", inv.OrderId);
        var c = f.Calls[0];
        Assert.Equal("POST", c.Method);
        Assert.Equal("https://example.test/api/v1/invoices", c.Url);
        using var body = JsonDocument.Parse(c.Body!);
        var b = body.RootElement;
        Assert.Equal("9.50", b.GetProperty("amount").GetString());
        Assert.Equal("EUR", b.GetProperty("currency").GetString());
        Assert.True(b.GetProperty("test").GetBoolean());
        Assert.Equal("Zoë <b>", b.GetProperty("metadata").GetProperty("name").GetString());
        Assert.False(b.TryGetProperty("description", out _));
        Assert.StartsWith("sdk_", b.GetProperty("idempotencyKey").GetString());
        Assert.Equal(Sig("sk_unit", c.Headers["x-timestamp"], c.Body!), c.Headers["x-signature"]);
        Assert.Equal("pk_unit_000001", c.Headers["x-api-key"]);
        Assert.Equal("application/json", c.Headers["Content-Type"]);
    }

    [Fact]
    public async Task AnonymousMetadata()
    {
        var f = new FakeHandler(FakeHandler.Answer(201, "{\"id\":\"" + new string('a', 24) + "\",\"invoice_url\":\"u\"}"));
        await Client(f).CreateInvoiceAsync(new InvoiceRequest { Amount = 1m, Metadata = new { userId = 42, plan = "pro" } });
        using var body = JsonDocument.Parse(f.Calls[0].Body!);
        Assert.Equal(42, body.RootElement.GetProperty("metadata").GetProperty("userId").GetInt32());
    }

    [Fact]
    public async Task GetSignedOverPathWithBasePath()
    {
        var b24 = new string('b', 24);
        var f = new FakeHandler(FakeHandler.Answer(200, "{\"invoiceId\":\"" + b24 + "\",\"status\":\"PAID\",\"amountFiat\":1,\"currency\":\"USD\",\"test\":true,"
            + "\"settledCurrency\":\"USDT_TRC20\",\"amountReceived\":\"1.000000\",\"settledAt\":\"2026-11-14T00:00:00Z\",\"underpaidWithinTolerance\":true,\"metadata\":{\"k\":\"v\"},\"confirmations\":3}"));
        var inv = await Client(f, "https://example.test/gw/").GetInvoiceAsync(b24);
        Assert.Equal(b24, inv.Id);
        Assert.True(inv.IsPaid);
        Assert.True(inv.Test);
        Assert.Equal("USDT_TRC20", inv.Asset);
        Assert.Equal("1.000000", inv.PaidCrypto);
        Assert.Equal("0", inv.ConfirmedCrypto);
        Assert.Equal("2026-11-14T00:00:00Z", inv.PaidAt);
        Assert.True(inv.UnderpaidWithinTolerance);
        Assert.Equal("v", inv.Metadata.GetProperty("k").GetString());
        Assert.Equal(3, inv.Raw.GetProperty("confirmations").GetInt32());
        Assert.True(inv.Matches(1.00m, "USD"));
        var c = f.Calls[0];
        Assert.Equal("https://example.test/gw/api/v1/invoices/" + b24, c.Url);
        Assert.Equal(ArionPayWebhook.HmacHex("sk_unit", Encoding.UTF8.GetBytes(c.Headers["x-timestamp"] + ".GET /gw/api/v1/invoices/" + b24)), c.Headers["x-signature"]);
        Assert.Null(c.Body);
    }

    [Fact]
    public async Task ReplayResignedOnce()
    {
        var c24 = new string('c', 24);
        var used = FakeHandler.Answer(401, "{\"error\":\"This signed request was already used. Sign each request with a fresh timestamp.\"}");
        var f = new FakeHandler(used, FakeHandler.Answer(200, "{\"invoiceId\":\"" + c24 + "\",\"status\":\"NEW\"}"));
        Assert.Equal("new", (await Client(f).GetInvoiceAsync(c24)).Status);
        Assert.Equal(2, f.Calls.Count);
        Assert.NotEqual(f.Calls[0].Headers["x-timestamp"], f.Calls[1].Headers["x-timestamp"]);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(1100) }, _waits);
        var f2 = new FakeHandler(used, used);
        Assert.Equal(401, (await Assert.ThrowsAsync<ArionPayApiException>(() => Client(f2).GetInvoiceAsync(c24))).Status);
        Assert.Equal(2, f2.Calls.Count);
    }

    [Fact]
    public async Task CreateRetryKeepsIdempotencyKey()
    {
        var f = new FakeHandler(new HttpRequestException("Connection refused", new SocketException(111)), FakeHandler.Answer(503, "{\"error\":\"busy\"}"),
            FakeHandler.Answer(201, "{\"id\":\"" + new string('d', 24) + "\",\"invoice_url\":\"u\",\"data\":{}}"));
        var inv = await Client(f).CreateInvoiceAsync(new InvoiceRequest { Amount = 1m });
        Assert.Equal("new", inv.Status);
        Assert.Equal("USD", inv.Currency);
        Assert.Equal(3, f.Calls.Count);
        Assert.Equal(f.Calls[0].Body, f.Calls[2].Body);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500) }, _waits);
    }

    [Fact]
    public async Task RateLimit()
    {
        var e24 = new string('e', 24);
        var f = new FakeHandler(FakeHandler.Answer(429, "{\"error\":\"slow down\",\"code\":\"rate_limited\"}", "2"), FakeHandler.Answer(200, "{\"invoiceId\":\"" + e24 + "\",\"status\":\"NEW\"}"));
        await Client(f).GetInvoiceAsync(e24);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, _waits);
        var f2 = new FakeHandler(FakeHandler.Answer(429, "{\"error\":\"slow down\",\"code\":\"rate_limited\",\"retryAfterSec\":60}"));
        var e = await Assert.ThrowsAsync<ArionPayApiException>(() => Client(f2).GetInvoiceAsync(e24));
        Assert.Equal(429, e.Status);
        Assert.Equal("rate_limited", e.Code);
        Assert.Equal(60, e.RetryAfterSeconds);
        Assert.Single(f2.Calls);
    }

    [Fact]
    public async Task NoRetryForSimulateAndCodes()
    {
        var f = new FakeHandler(FakeHandler.Answer(503, "{\"error\":\"busy\"}"));
        Assert.Equal(503, (await Assert.ThrowsAsync<ArionPayApiException>(() => Client(f).SimulatePaymentAsync(new string('e', 24), "exact"))).Status);
        Assert.Single(f.Calls);
        var f2 = new FakeHandler(FakeHandler.Answer(409, "{\"error\":\"This Idempotency-Key was already used\",\"code\":\"idempotency_key_reused\"}"));
        var e = await Assert.ThrowsAsync<ArionPayApiException>(() => Client(f2).CreateInvoiceAsync(new InvoiceRequest { Amount = 1m, IdempotencyKey = "k" }));
        Assert.Equal(409, e.Status);
        Assert.Equal("idempotency_key_reused", e.Code);
        Assert.Single(f2.Calls);
        Assert.Contains("(409)", e.Message);
        Assert.DoesNotContain("sk_unit", e.Message);
    }

    [Fact]
    public async Task Unreachable()
    {
        var f = new FakeHandler(new HttpRequestException("down"), new TaskCanceledException("timeout"));
        var e = await Assert.ThrowsAsync<ArionPayApiException>(() => Client(f, "https://example.test", 2).GetInvoiceAsync(new string('f', 24)));
        Assert.Equal(0, e.Status);
        Assert.Equal("network_error", e.Code);
        Assert.Contains("Could not reach", e.Message);
        Assert.Equal(2, f.Calls.Count);
    }

    [Fact]
    public async Task CallerCancellationIsNotANetworkError()
    {
        var f = new FakeHandler(new TaskCanceledException("cancelled"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(f).GetInvoiceAsync(new string('f', 24), cts.Token));
    }

    [Fact]
    public async Task NonJsonErrorPage()
    {
        var f = new FakeHandler(FakeHandler.Answer(502, "<html>Bad gateway</html>"), FakeHandler.Answer(502, "<html>"), FakeHandler.Answer(502, ""));
        var e = await Assert.ThrowsAsync<ArionPayApiException>(() => Client(f).GetInvoiceAsync(new string('f', 24)));
        Assert.Equal(502, e.Status);
        Assert.Equal("ArionPay API error (502): HTTP 502", e.Message);
    }

    [Fact]
    public async Task BadInputNeverReachesNetwork()
    {
        var f = new FakeHandler();
        var c = Client(f);
        await Assert.ThrowsAsync<ArgumentException>(() => c.GetInvoiceAsync("../admin"));
        await Assert.ThrowsAsync<ArgumentException>(() => c.GetInvoiceAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => c.SimulatePaymentAsync(new string('a', 24), "free_money"));
        await Assert.ThrowsAsync<ArgumentException>(() => c.CreateInvoiceAsync(new InvoiceRequest()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => c.CreateInvoiceAsync(null!));
        Assert.Empty(f.Calls);
    }

    [Fact]
    public async Task ReadsAndTestWebhook()
    {
        var f = new FakeHandler(FakeHandler.Answer(200, "{\"currencies\":[{\"symbol\":\"USDT_TRC20\",\"usdRate\":1},\"junk\"]}"),
            FakeHandler.Answer(200, "{\"currency\":\"EUR\"}"), FakeHandler.Answer(200, "{\"delivered\":true,\"eventId\":\"evt_test_1\"}"));
        var c = Client(f);
        var cur = await c.GetCurrenciesAsync();
        Assert.Single(cur);
        Assert.Equal("USDT_TRC20", cur[0].GetProperty("symbol").GetString());
        Assert.False(f.Calls[0].Headers.ContainsKey("x-signature"));
        Assert.Equal("EUR", (await c.GetRatesAsync("EUR")).GetProperty("currency").GetString());
        Assert.EndsWith("/api/v1/rates?currency=EUR", f.Calls[1].Url);
        Assert.True((await c.SendTestWebhookAsync()).GetProperty("delivered").GetBoolean());
        using var sent = JsonDocument.Parse(f.Calls[2].Body!);
        Assert.Equal("invoice.paid", sent.RootElement.GetProperty("eventType").GetString());
    }

    [Fact]
    public async Task ClientVerifyWebhook()
    {
        using var c = new ArionPayClient(new ArionPayOptions { ApiKey = "pk_abcdefgh", ApiSecret = "s", WebhookSecret = WebhookTests.Secret });
        var body = Encoding.UTF8.GetBytes(WebhookTests.Event);
        Assert.Equal("invoice.paid", c.VerifyWebhook(body, WebhookTests.Signed(WebhookTests.Event)).Type);
        Assert.Equal("evt_abc", (await c.VerifyWebhookAsync(new MemoryStream(body), WebhookTests.Signed(WebhookTests.Event))).Id);
        await Assert.ThrowsAsync<ArionPaySignatureException>(() => c.VerifyWebhookAsync(new MemoryStream(new byte[(1 << 20) + 1]), "t=1,v1=" + new string('a', 64)));
    }

    /// <summary>The real HttpClient against a real local HTTP server.</summary>
    [Fact]
    public async Task RealHttpTransport()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var seen = new List<(string Method, string Path, Dictionary<string, string> Headers, byte[] Body)>();
        var serve = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                using var ms = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(ms);
                var h = ctx.Request.Headers.AllKeys.ToDictionary(k => k!.ToLowerInvariant(), k => ctx.Request.Headers[k]!);
                lock (seen) seen.Add((ctx.Request.HttpMethod, ctx.Request.RawUrl!, h, ms.ToArray()));
                string json;
                if (ctx.Request.RawUrl!.Contains("/redirect/"))
                {
                    ctx.Response.StatusCode = 302;
                    ctx.Response.RedirectLocation = "https://evil.test/";
                    json = "";
                }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    ctx.Response.StatusCode = 201;
                    json = "{\"id\":\"" + new string('a', 24) + "\",\"invoice_url\":\"https://pay.test/pay/x\",\"data\":{}}";
                }
                else
                {
                    json = "{\"invoiceId\":\"" + new string('b', 24) + "\",\"status\":\"NEW\"}";
                }
                var bytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
        var baseUrl = $"http://127.0.0.1:{port}/gw";
        using var c = new ArionPayClient(new ArionPayOptions { ApiKey = "pk_unit_000001", ApiSecret = "sk_unit", BaseUrl = baseUrl, AppInfo = "t/1" });
        Assert.Equal("https://pay.test/pay/x", (await c.CreateInvoiceAsync(new InvoiceRequest { Amount = 2.00m, OrderId = "é-1" })).InvoiceUrl);
        Assert.Equal("new", (await c.GetInvoiceAsync(new string('b', 24))).Status);
        var post = seen[0];
        Assert.Equal("/gw/api/v1/invoices", post.Path);
        Assert.StartsWith("application/json", post.Headers["content-type"]);
        Assert.StartsWith("arionpay-dotnet/", post.Headers["user-agent"]);
        Assert.Equal(Sig("sk_unit", post.Headers["x-timestamp"], post.Body), post.Headers["x-signature"]);
        using (var doc = JsonDocument.Parse(post.Body)) Assert.Equal("é-1", doc.RootElement.GetProperty("orderId").GetString());
        var get = seen[1];
        Assert.Equal("GET", get.Method);
        Assert.Empty(get.Body);
        Assert.Equal(ArionPayWebhook.HmacHex("sk_unit", Encoding.UTF8.GetBytes(get.Headers["x-timestamp"] + ".GET /gw/api/v1/invoices/" + new string('b', 24))), get.Headers["x-signature"]);
        // redirects are never followed (the signature must not travel to another host)
        using var r = new ArionPayClient(new ArionPayOptions { ApiKey = "pk_unit_000001", ApiSecret = "sk_unit", BaseUrl = baseUrl + "/redirect" });
        Assert.Equal(302, (await Assert.ThrowsAsync<ArionPayApiException>(() => r.GetInvoiceAsync(new string('f', 24)))).Status);
        // nothing listening: status 0
        using var down = new ArionPayClient(new ArionPayOptions { ApiKey = "pk_unit_000001", ApiSecret = "sk_unit", BaseUrl = $"http://127.0.0.1:{FreePort()}", MaxAttempts = 1 });
        Assert.Equal(0, (await Assert.ThrowsAsync<ArionPayApiException>(() => down.GetInvoiceAsync(new string('f', 24)))).Status);
        listener.Stop();
        await serve;
    }

    internal static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
