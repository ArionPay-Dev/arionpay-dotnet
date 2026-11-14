// A minimal ASP.NET Core shop: /pay/{order} sends the customer to ArionPay, /arionpay/webhook marks the order paid.
//
//   ARIONPAY_API_KEY=pk_... ARIONPAY_API_SECRET=sk_... ARIONPAY_WEBHOOK_SECRET=whsec_... \
//   PUBLIC_URL=https://shop.example.com dotnet run --project examples/WebhookShop
using System.Collections.Concurrent;
using ArionPay;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(new ArionPayClient(new ArionPayOptions
{
    ApiKey = builder.Configuration["ARIONPAY_API_KEY"] ?? "",
    ApiSecret = builder.Configuration["ARIONPAY_API_SECRET"] ?? "",
    WebhookSecret = builder.Configuration["ARIONPAY_WEBHOOK_SECRET"],
    BaseUrl = builder.Configuration["ARIONPAY_BASE_URL"] ?? ArionPayClient.DefaultBaseUrl,
    AppInfo = "example-shop/1.0",
}));
var app = builder.Build();

var publicUrl = (app.Configuration["PUBLIC_URL"] ?? "http://localhost:5000").TrimEnd('/');
var testMode = app.Configuration["ARIONPAY_LIVE"] != "1";
var orders = new Dictionary<string, (decimal Total, string Currency)> { ["4001"] = (49.90m, "USD") }; // use your database
var paid = new ConcurrentDictionary<string, bool>();
var processed = new ConcurrentDictionary<string, bool>(); // event ids already handled

app.MapGet("/pay/{orderId}", async (string orderId, ArionPayClient arionpay, CancellationToken ct) =>
{
    if (!orders.TryGetValue(orderId, out var order)) return Results.NotFound("No such order");
    try
    {
        var invoice = await arionpay.CreateInvoiceAsync(new InvoiceRequest
        {
            Amount = order.Total,
            Currency = order.Currency,
            OrderId = orderId,
            SuccessUrl = $"{publicUrl}/thanks/{orderId}",
            CancelUrl = $"{publicUrl}/cart",
            CallbackUrl = $"{publicUrl}/arionpay/webhook",
            IdempotencyKey = $"order-{orderId}-{order.Total}{order.Currency}", // one invoice per order total
            Test = testMode,
        }, ct);
        return Results.Redirect(invoice.InvoiceUrl);
    }
    catch (ArionPayApiException e)
    {
        app.Logger.LogError("ArionPay: {Message}", e.Message);
        return Results.Problem("The crypto payment could not be started. Please try again in a minute.", statusCode: 502);
    }
});

app.MapPost("/arionpay/webhook", async (HttpRequest request, ArionPayClient arionpay, CancellationToken ct) =>
{
    WebhookEvent ev;
    try
    {
        ev = await arionpay.VerifyWebhookAsync(request.Body, request.Headers["ArionPay-Signature"], ct); // the raw body
    }
    catch (ArionPaySignatureException)
    {
        return Results.Unauthorized();
    }
    if (ArionPayWebhook.IsTestEvent(ev)) return Results.Ok(); // "Send test webhook": a made-up invoice
    var inv = ev.Invoice;
    if (inv.Test != testMode) return Results.Conflict("test/live mismatch");
    if (processed.TryAdd(ev.Id, true)
        && ev.Type == "invoice.paid"
        && orders.TryGetValue(inv.OrderId, out var order)
        && inv.Matches(order.Total, order.Currency)
        && paid.TryAdd(inv.OrderId, true))
    {
        // deliver here
        app.Logger.LogWarning("order {Order} paid with {Paid} {Asset} (invoice {Invoice})", inv.OrderId, inv.PaidCrypto, inv.Asset, inv.Id);
    }
    return Results.Ok();
});

app.Run();
