# ArionPay for .NET

Official .NET client for [ArionPay](https://arionpay.com). Create crypto invoices, send customers to the payment page, and check webhooks. The money goes straight to your own wallet: ArionPay never holds it.

- No dependencies: only .NET itself (`HttpClient`, `System.Text.Json`). .NET 8 or newer (8, 9, 10).
- Works with ASP.NET Core (minimal APIs and controllers), worker services and console apps.
- Async all the way, with `CancellationToken` on every call.
- Safe retries: every invoice request carries an idempotency key, so a network error can never create two invoices.
- Amounts are exact: prices are `decimal`, crypto amounts are kept as text.

```bash
dotnet add package ArionPay
```

## Keys

In your ArionPay dashboard, open your store:

| Value | Where | Starts with |
| --- | --- | --- |
| API key | Store page, **API key** | `pk_` |
| API secret | Store page, **API secret** (asks for your authenticator code) | `sk_` |
| Webhook secret | **Webhooks** page, **signing secret** | `whsec_` |

Keep them on your server, for example in environment variables or user secrets.

## Take a payment

```csharp
using ArionPay;

builder.Services.AddSingleton(new ArionPayClient(new ArionPayOptions
{
    ApiKey = builder.Configuration["ArionPay:ApiKey"]!,
    ApiSecret = builder.Configuration["ArionPay:ApiSecret"]!,
    WebhookSecret = builder.Configuration["ArionPay:WebhookSecret"],
}));

app.MapGet("/pay/{orderId}", async (string orderId, ArionPayClient arionpay, CancellationToken ct) =>
{
    var invoice = await arionpay.CreateInvoiceAsync(new InvoiceRequest
    {
        Amount = 49.90m,
        Currency = "USD",
        OrderId = orderId,
        SuccessUrl = $"https://shop.example.com/thanks/{orderId}",
        CancelUrl = "https://shop.example.com/cart",
        Metadata = new { userId = 42 },
    }, ct);
    return Results.Redirect(invoice.InvoiceUrl);
});
```

The customer coming back to `SuccessUrl` does **not** prove they paid. Deliver the order when the `invoice.paid` webhook arrives (or when `GetInvoiceAsync` says paid).

A webhook's `ev.Invoice` and the result of `GetInvoiceAsync(id)` are the same `Invoice` class (`Id`, `Status`, `OrderId`, `AmountFiat`, `Currency`, `Asset`, `PaidCrypto`, `Test`, `Metadata`, ...), so one method can handle both. `Raw` has every field ArionPay sent.

## Webhooks

Set your webhook address on the **Webhooks** page of the dashboard (a public `https://` address), or per invoice with `CallbackUrl`. Check the signature against the **raw** body, before you parse it.

### ASP.NET Core minimal API

```csharp
app.MapPost("/arionpay/webhook", async (HttpRequest request, ArionPayClient arionpay, CancellationToken ct) =>
{
    WebhookEvent ev;
    try
    {
        ev = await arionpay.VerifyWebhookAsync(request.Body, request.Headers["ArionPay-Signature"], ct);
    }
    catch (ArionPaySignatureException)
    {
        return Results.Unauthorized();
    }
    if (!ArionPayWebhook.IsTestEvent(ev)) // "Send test webhook": not a real order
        await HandleEventAsync(ev);
    return Results.Ok();
});
```

Read the body yourself as above. Do not bind it to a model class: that is parsed and re-encoded JSON, and its signature never matches.

### MVC controller

```csharp
[HttpPost("/arionpay/webhook")]
public async Task<IActionResult> Webhook(CancellationToken ct)
{
    try
    {
        var ev = await _arionpay.VerifyWebhookAsync(Request.Body, Request.Headers["ArionPay-Signature"], ct);
        if (!ArionPayWebhook.IsTestEvent(ev)) await HandleEventAsync(ev);
        return Ok();
    }
    catch (ArionPaySignatureException)
    {
        return Unauthorized();
    }
}
```

Leave this action out of antiforgery validation (it is called by ArionPay, not a browser form). Already have the bytes? `arionpay.VerifyWebhook(bytes, header)` or `ArionPayWebhook.Verify(bytes, header, webhookSecret)`.

### Handling an event

```csharp
async Task HandleEventAsync(WebhookEvent ev)
{
    if (await db.ProcessedEvents.AnyAsync(x => x.Id == ev.Id)) return; // the same event can arrive more than once
    var inv = ev.Invoice;
    var order = await db.Orders.FirstOrDefaultAsync(o => o.Number == inv.OrderId);
    if (ev.Type == "invoice.paid" && order is { IsPaid: false }
        && inv.Matches(order.Total, order.Currency) && inv.Test == order.IsTest)
    {
        order.MarkPaid(inv); // deliver here
    }
    db.ProcessedEvents.Add(new ProcessedEvent { Id = ev.Id });
    await db.SaveChangesAsync();
}
```

`inv.Matches(49.90m, "USD")` is true when the invoice price is that total, to the cent, in that currency.

| Event | Meaning | What to do |
| --- | --- | --- |
| `invoice.pending` | A payment was seen on the network and waits for confirmations. | Show "payment on its way". Do not deliver yet. |
| `invoice.partial` | Less than the full amount arrived. The invoice stays open for the rest. | Do not deliver. |
| `invoice.paid` | Fully paid and confirmed. Also sent when you accept a payment by hand in the dashboard. | Deliver the order. |
| `invoice.expired` | Time ran out without full payment. A late payment can still make it `paid`. | Keep the order unpaid. |
| `invoice.needs_review` | Something needs a person, for example more money arrived after the invoice was paid. | Tell a human. Do not change the order. |

ArionPay sends again when your server does not answer `2xx` (for about 3 days).

### Rules for a safe integration

1. Check the signature on the raw body. Never trust a webhook you could not verify.
2. Deliver only on `invoice.paid` (or when `(await GetInvoiceAsync(id)).IsPaid`).
3. Match the invoice to your order: your `OrderId` or `Metadata`, and the same amount and currency as the order's current total (`inv.Matches`).
4. Ignore test events (`ArionPayWebhook.IsTestEvent(ev)`), and in your live shop ignore invoices with `Test == true`.
5. Handle each `ev.Id` once, and answer `2xx` quickly.
6. Do not compare crypto amounts yourself. ArionPay decides when an invoice is paid, including payments inside your store's payment tolerance and payments you accept in Payment Radar.

## Test without real money

```csharp
var inv = await arionpay.CreateInvoiceAsync(new InvoiceRequest { Amount = 10m, OrderId = "test-1", Asset = "USDT_TRC20", Test = true });
await arionpay.SimulatePaymentAsync(inv.Id, "exact"); // your webhook gets invoice.paid, like a real payment
```

Scenarios: `exact`, `detected`, `confirm`, `underpaid_1pct`, `underpaid_5pct`, `overpaid`, `partial_half`, `late`.

`await arionpay.SendTestWebhookAsync("invoice.paid")` sends a signed sample event to your store's webhook address. It describes a made-up invoice (`inv_test_...`) and must never change an order.

A complete small shop is in [`examples/WebhookShop`](examples/WebhookShop/Program.cs).

## Reference

| Method | Returns |
| --- | --- |
| `CreateInvoiceAsync(InvoiceRequest)` | `CreatedInvoice`: `Id`, `InvoiceUrl`, `Status`, `AmountFiat`, `Currency`, `Asset`, `Address`, `AmountCrypto`, `ExpiresAt`, `OrderId`, `Raw` |
| `GetInvoiceAsync(id)` | `Invoice`, with the webhook field names. |
| `SimulatePaymentAsync(id, scenario)` | Sandbox only (test invoices). |
| `SendTestWebhookAsync(eventType)` | `JsonElement` with `delivered`, `httpStatus`, `eventId` |
| `GetCurrenciesAsync()` | Coins you can use as `Asset` / `AllowedAssets`. |
| `GetRatesAsync(currency)` | Current coin prices. |
| `VerifyWebhookAsync(stream, header)` / `VerifyWebhook(bytes, header)` | `WebhookEvent`, or throws `ArionPaySignatureException`. |

`InvoiceRequest`: `Amount` (required), `Currency` (default `USD`), `OrderId`, `Description`, `CustomerEmail`, `SuccessUrl`, `CancelUrl`, `CallbackUrl`, `Asset`, `AllowedAssets`, `ExpiresMin` (5 to 1440), `Metadata` (a dictionary or an anonymous object), `IdempotencyKey`, `Test`. Null fields are not sent.

Errors from ArionPay are `ArionPayApiException` with `Status` (HTTP status, `0` when ArionPay could not be reached), `Code` (for example `idempotency_key_reused`, `rate_limited`, `unknown_asset`), `RetryAfterSeconds` and `Message`. Messages never contain your secret. Bad arguments throw `ArgumentException` before anything is sent. Cancelling your `CancellationToken` throws `OperationCanceledException` as usual.

`ArionPayOptions`: `ApiKey`, `ApiSecret`, `WebhookSecret`, `BaseUrl` (default `https://merchant.arionpay.com`), `Timeout` (default 20 seconds), `MaxAttempts` (default 3), `AppInfo` (added to the User-Agent). You may pass your own `HttpClient` (for example from `IHttpClientFactory`) as the second constructor argument; it should not follow redirects. The client is thread-safe: register it once as a singleton.

## Build and test

```bash
dotnet test                      # unit tests; the live test is skipped
ARIONPAY_IT_BASE_URL=... ARIONPAY_IT_API_KEY=pk_... ARIONPAY_IT_API_SECRET=sk_... \
ARIONPAY_IT_WEBHOOK_SECRET=whsec_... dotnet test
dotnet pack src/ArionPay -c Release -o dist
```

## License

MIT
