using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ArionPay.Tests;

/// <summary>A test that runs only when ARIONPAY_IT_BASE_URL is set.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ARIONPAY_IT_BASE_URL")))
            Skip = "set ARIONPAY_IT_BASE_URL to run against a real server";
    }
}

/// <summary>
/// Runs against a real ArionPay server:
/// ARIONPAY_IT_BASE_URL=http://127.0.0.1:18090 ARIONPAY_IT_API_KEY=pk_... ARIONPAY_IT_API_SECRET=sk_... ARIONPAY_IT_WEBHOOK_SECRET=whsec_... dotnet test
/// The store's webhook address must be http://127.0.0.1:&lt;ARIONPAY_IT_HOOK_PORT, default 18099&gt;/hook (a dev server
/// that allows private webhook addresses).
/// </summary>
public class IntegrationTests
{
    private readonly ITestOutputHelper _out;
    private readonly List<WebhookEvent> _received = new();
    private readonly List<string> _refused = new();

    public IntegrationTests(ITestOutputHelper output) => _out = output;

    private async Task<WebhookEvent> WaitFor(string what, Func<WebhookEvent, bool> p)
    {
        var end = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < end)
        {
            lock (_received)
            {
                var hit = _received.FirstOrDefault(p);
                if (hit is not null) return hit;
            }
            await Task.Delay(300);
        }
        lock (_received) throw new Xunit.Sdk.XunitException($"{what} not received; got [{string.Join(",", _received.Select(e => e.Type))}], refused [{string.Join(";", _refused)}]");
    }

    [LiveFact]
    public async Task FullSandboxPaymentWithRealWebhook()
    {
        Func<string, string?> env = Environment.GetEnvironmentVariable;
        var baseUrl = env("ARIONPAY_IT_BASE_URL")!;
        var port = int.Parse(env("ARIONPAY_IT_HOOK_PORT") ?? "18099");
        var hook = $"http://127.0.0.1:{port}/hook";
        using var ap = new ArionPayClient(new ArionPayOptions
        {
            ApiKey = env("ARIONPAY_IT_API_KEY")!, ApiSecret = env("ARIONPAY_IT_API_SECRET")!, WebhookSecret = env("ARIONPAY_IT_WEBHOOK_SECRET"),
            BaseUrl = baseUrl, AppInfo = "dotnet-integration-test",
        });

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/hook/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                try
                {
                    var ev = await ap.VerifyWebhookAsync(ctx.Request.InputStream, ctx.Request.Headers["ArionPay-Signature"]);
                    lock (_received) _received.Add(ev);
                    ctx.Response.StatusCode = 200;
                }
                catch (ArionPaySignatureException e)
                {
                    lock (_received) _refused.Add(e.Message);
                    ctx.Response.StatusCode = 401;
                }
                ctx.Response.Close();
            }
        });

        try
        {
            Assert.Contains(await ap.GetCurrenciesAsync(), c => c.GetProperty("symbol").GetString() == "USDT_TRC20");
            Assert.NotEmpty((await ap.GetRatesAsync("EUR")).EnumerateObject());

            var orderId = "dotnet-it-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var inv = await ap.CreateInvoiceAsync(new InvoiceRequest
            {
                Amount = 7.25m, Currency = "USD", OrderId = orderId, Asset = "USDT_TRC20", Test = true, CallbackUrl = hook,
                Metadata = new { cart = "dotnet", n = 3 },
            });
            Assert.Matches("^[0-9a-f]{24}$", inv.Id);
            Assert.EndsWith("/pay/" + inv.Id, inv.InvoiceUrl);
            Assert.Equal(orderId, inv.OrderId);

            var key = "dotnet-key-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var a = await ap.CreateInvoiceAsync(new InvoiceRequest { Amount = 5m, OrderId = orderId + "-b", IdempotencyKey = key, Test = true });
            var b = await ap.CreateInvoiceAsync(new InvoiceRequest { Amount = 5m, OrderId = orderId + "-b", IdempotencyKey = key, Test = true });
            Assert.Equal(a.Id, b.Id);
            var reused = await Assert.ThrowsAsync<ArionPayApiException>(() => ap.CreateInvoiceAsync(new InvoiceRequest { Amount = 6m, OrderId = orderId + "-b", IdempotencyKey = key, Test = true }));
            Assert.Equal("idempotency_key_reused", reused.Code);

            var read = await ap.GetInvoiceAsync(inv.Id);
            Assert.Equal("new", read.Status);
            Assert.Equal("dotnet", read.Metadata.GetProperty("cart").GetString());
            Assert.Equal(3, read.Metadata.GetProperty("n").GetInt32());
            Assert.True(read.Test);

            await ap.SimulatePaymentAsync(inv.Id, "detected");
            var pending = await WaitFor("invoice.pending", e => e.Type == "invoice.pending" && e.Invoice.Id == inv.Id);
            Assert.Equal("pending", pending.Invoice.Status);
            await ap.SimulatePaymentAsync(inv.Id, "confirm");
            var paid = await WaitFor("invoice.paid", e => e.Type == "invoice.paid" && e.Invoice.Id == inv.Id);
            var pi = paid.Invoice;
            Assert.Equal(orderId, pi.OrderId);
            Assert.True(pi.Test);
            Assert.False(ArionPayWebhook.IsTestEvent(paid));
            Assert.True(pi.Matches(7.25m, "USD"));
            Assert.Equal("USDT_TRC20", pi.Asset);
            Assert.Equal("dotnet", pi.Metadata.GetProperty("cart").GetString());

            read = await ap.GetInvoiceAsync(inv.Id);
            Assert.True(read.IsPaid);
            Assert.True(read.Matches(7.25m, "USD"));
            Assert.NotEqual("0", read.PaidCrypto);

            var under = await ap.CreateInvoiceAsync(new InvoiceRequest { Amount = 20m, OrderId = orderId + "-u", Asset = "USDT_TRC20", Test = true, CallbackUrl = hook });
            await ap.SimulatePaymentAsync(under.Id, "underpaid_1pct");
            var up = await WaitFor("tolerance invoice.paid", e => e.Type == "invoice.paid" && e.Invoice.Id == under.Id);
            Assert.True(up.Invoice.UnderpaidWithinTolerance);
            Assert.True(up.Invoice.Matches(20m, "USD"));

            var t = await ap.SendTestWebhookAsync("invoice.expired");
            Assert.True(t.GetProperty("delivered").GetBoolean(), t.ToString());
            var testId = t.GetProperty("eventId").GetString();
            Assert.True(ArionPayWebhook.IsTestEvent(await WaitFor("test event", e => e.Id == testId)));

            Assert.Equal(404, (await Assert.ThrowsAsync<ArionPayApiException>(() => ap.GetInvoiceAsync(new string('0', 24)))).Status);
            using var wrong = new ArionPayClient(new ArionPayOptions { ApiKey = env("ARIONPAY_IT_API_KEY")!, ApiSecret = "sk_wrong", BaseUrl = baseUrl });
            Assert.Equal(401, (await Assert.ThrowsAsync<ArionPayApiException>(() => wrong.GetInvoiceAsync(inv.Id))).Status);
            lock (_received) Assert.Empty(_refused);
            _out.WriteLine($"invoice {inv.Id} paid, {_received.Count} signed webhooks verified");
        }
        finally
        {
            listener.Stop();
            await serve;
        }
    }
}
