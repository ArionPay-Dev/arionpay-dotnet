using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ArionPay;

/// <summary>Settings of an <see cref="ArionPayClient"/>. ApiKey and ApiSecret are required.</summary>
public sealed class ArionPayOptions
{
    /// <summary>pk_... (ArionPay dashboard &gt; your store).</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>sk_...</summary>
    public string ApiSecret { get; set; } = "";
    /// <summary>whsec_... (only needed for <see cref="ArionPayClient.VerifyWebhook"/>).</summary>
    public string? WebhookSecret { get; set; }
    /// <summary>Default https://merchant.arionpay.com.</summary>
    public string BaseUrl { get; set; } = ArionPayClient.DefaultBaseUrl;
    /// <summary>Per request (default 20 seconds).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);
    /// <summary>Tries for safe requests on network errors, 502/503/504 and short 429 (default 3, max 5).</summary>
    public int MaxAttempts { get; set; } = 3;
    /// <summary>Added to the User-Agent, e.g. "myshop/2.1".</summary>
    public string? AppInfo { get; set; }
}

/// <summary>
/// ArionPay merchant API client: create crypto invoices, read them, and verify webhooks. Payments go straight to the
/// merchant's own wallet. Use it on your server only: it holds your API secret. Thread-safe: register it once
/// (a singleton) and share it.
/// </summary>
public sealed class ArionPayClient : IDisposable
{
    /// <summary>Version of this library.</summary>
    public const string Version = "1.0.0";
    /// <summary>ArionPay's merchant API.</summary>
    public const string DefaultBaseUrl = "https://merchant.arionpay.com";
    /// <summary>Scenarios for <see cref="SimulatePaymentAsync"/> (test invoices only).</summary>
    public static readonly IReadOnlyList<string> Scenarios = new[] { "detected", "confirm", "exact", "underpaid_1pct", "underpaid_5pct", "overpaid", "partial_half", "late" };

    private static readonly Regex Pk = new("^pk_[A-Za-z0-9_-]{8,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex Id = new("^[0-9a-fA-F]{24}$", RegexOptions.CultureInvariant);
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _webhookSecret;
    private readonly string _origin;
    private readonly string _basePath;
    private readonly TimeSpan _timeout;
    private readonly int _maxAttempts;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    internal Func<TimeSpan, CancellationToken, Task> Delay = Task.Delay;
    internal Func<long> Clock = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>The User-Agent sent with every request.</summary>
    public string UserAgent { get; }

    /// <summary>Creates a client.</summary>
    /// <param name="options">Your keys and settings.</param>
    /// <param name="httpClient">Your own HttpClient (e.g. from IHttpClientFactory); it should not follow redirects.</param>
    public ArionPayClient(ArionPayOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ApiKey is null || !Pk.IsMatch(options.ApiKey))
            throw new ArgumentException("ApiKey must be your store API key (it starts with pk_).", nameof(options));
        if (string.IsNullOrEmpty(options.ApiSecret))
            throw new ArgumentException("ApiSecret is required (your store API secret, it starts with sk_).", nameof(options));
        if (!Uri.TryCreate(string.IsNullOrEmpty(options.BaseUrl) ? DefaultBaseUrl : options.BaseUrl, UriKind.Absolute, out var u)
            || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(u.Host))
            throw new ArgumentException("BaseUrl must be an http(s) address.", nameof(options));
        _apiKey = options.ApiKey;
        _apiSecret = options.ApiSecret;
        _webhookSecret = options.WebhookSecret ?? "";
        _origin = u.GetLeftPart(UriPartial.Authority);
        _basePath = u.AbsolutePath.TrimEnd('/');
        _timeout = options.Timeout > TimeSpan.Zero ? options.Timeout : TimeSpan.FromSeconds(20);
        _maxAttempts = Math.Clamp(options.MaxAttempts, 1, 5);
        UserAgent = $"arionpay-dotnet/{Version} dotnet/{Environment.Version}" + (string.IsNullOrEmpty(options.AppInfo) ? "" : " " + options.AppInfo);
        if (httpClient is null)
        {
            _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan, // each request has its own timeout
            };
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
        }
    }

    // ---------------------------------------------------------------------------------------------------- invoices

    /// <summary>
    /// Creates an invoice. Send the customer to <see cref="CreatedInvoice.InvoiceUrl"/>.
    /// Safe to retry: every call carries an idempotency key (a random one when you set none), so a repeat after a
    /// network error returns the same invoice. The customer coming back to the success URL does NOT prove payment:
    /// wait for the invoice.paid webhook.
    /// </summary>
    public async Task<CreatedInvoice> CreateInvoiceAsync(InvoiceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount <= 0) throw new ArgumentException("Amount must be more than 0.", nameof(request));
        var body = new Dictionary<string, object?>
        {
            ["amount"] = request.Amount.ToString(CultureInfo.InvariantCulture),
            ["currency"] = string.IsNullOrEmpty(request.Currency) ? "USD" : request.Currency,
        };
        void Put(string k, object? v) { if (v is not null) body[k] = v; }
        Put("orderId", request.OrderId);
        Put("description", request.Description);
        Put("customerEmail", request.CustomerEmail);
        Put("successUrl", request.SuccessUrl);
        Put("cancelUrl", request.CancelUrl);
        Put("callbackUrl", request.CallbackUrl);
        Put("asset", request.Asset);
        Put("allowedAssets", request.AllowedAssets);
        Put("expiresMin", request.ExpiresMin);
        Put("metadata", request.Metadata);
        body["idempotencyKey"] = string.IsNullOrEmpty(request.IdempotencyKey) ? "sdk_" + Guid.NewGuid().ToString("N") : request.IdempotencyKey;
        Put("test", request.Test);

        var r = await RequestAsync("POST", "/api/v1/invoices", body, retrySafe: true, signed: true, cancellationToken).ConfigureAwait(false);
        var d = r.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : r;
        string S(string name) => Invoice.Str(d, name).Length > 0 ? Invoice.Str(d, name) : Invoice.Str(r, name);
        return new CreatedInvoice
        {
            Id = S("id"),
            InvoiceUrl = S("invoice_url"),
            Status = Invoice.Str(d, "status") is { Length: > 0 } st ? st : "new",
            AmountFiat = Invoice.Dec(d, "amount_fiat"),
            Currency = Invoice.Str(d, "currency") is { Length: > 0 } c ? c : (string)body["currency"]!,
            Asset = Invoice.Str(d, "asset"),
            Address = Invoice.Str(d, "address"),
            AmountCrypto = Invoice.Str(d, "amount_crypto"),
            ExpiresAt = Invoice.Str(d, "expires_at"),
            OrderId = d.TryGetProperty("order_id", out _) ? Invoice.Str(d, "order_id") : request.OrderId ?? "",
            Raw = r,
        };
    }

    /// <summary>Reads an invoice, signed, straight from ArionPay (webhook field names). Use it to double-check, or to poll.</summary>
    public async Task<Invoice> GetInvoiceAsync(string invoiceId, CancellationToken cancellationToken = default)
    {
        CheckId(invoiceId);
        return Invoice.FromRead(await RequestAsync("GET", "/api/v1/invoices/" + invoiceId, null, true, true, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Sandbox: moves a TEST invoice through a payment scenario (see <see cref="Scenarios"/>); webhooks then fire as
    /// for a real payment. Create the test invoice with an Asset (e.g. "USDT_TRC20") so a coin is chosen.
    /// </summary>
    public Task<JsonElement> SimulatePaymentAsync(string invoiceId, string scenario = "exact", CancellationToken cancellationToken = default)
    {
        CheckId(invoiceId);
        if (!Scenarios.Contains(scenario))
            throw new ArgumentException("scenario must be one of: " + string.Join(", ", Scenarios), nameof(scenario));
        return RequestAsync("POST", $"/api/v1/invoices/{invoiceId}/simulate", new Dictionary<string, object?> { ["scenario"] = scenario }, false, true, cancellationToken);
    }

    /// <summary>Asks ArionPay to send a signed sample event to your store's webhook address. It never describes a real order.</summary>
    public Task<JsonElement> SendTestWebhookAsync(string eventType = "invoice.paid", CancellationToken cancellationToken = default) =>
        RequestAsync("POST", "/api/v1/webhooks/test", new Dictionary<string, object?> { ["eventType"] = string.IsNullOrEmpty(eventType) ? "invoice.paid" : eventType }, false, true, cancellationToken);

    /// <summary>Coins you can use as Asset / AllowedAssets ("USDT_TRC20", "BTC", "LTC", ...), with USD rates.</summary>
    public async Task<IReadOnlyList<JsonElement>> GetCurrenciesAsync(CancellationToken cancellationToken = default)
    {
        var r = await RequestAsync("GET", "/api/v1/currencies", null, true, false, cancellationToken).ConfigureAwait(false);
        if (!r.TryGetProperty("currencies", out var list) || list.ValueKind != JsonValueKind.Array) return Array.Empty<JsonElement>();
        return list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => x.Clone()).ToList();
    }

    /// <summary>Current coin prices in one fiat currency.</summary>
    public Task<JsonElement> GetRatesAsync(string currency = "USD", CancellationToken cancellationToken = default) =>
        RequestAsync("GET", "/api/v1/rates?currency=" + Uri.EscapeDataString(string.IsNullOrEmpty(currency) ? "USD" : currency), null, true, false, cancellationToken);

    /// <summary>Checks a webhook with the webhook secret of this client. See <see cref="ArionPayWebhook.Verify(byte[], string, string, long, long?)"/>.</summary>
    public WebhookEvent VerifyWebhook(byte[] rawBody, string? signatureHeader) =>
        ArionPayWebhook.Verify(rawBody, signatureHeader, _webhookSecret, ArionPayWebhook.DefaultToleranceSeconds, Clock());

    /// <summary>
    /// Reads the request body (at most 1 MB) and checks it: <c>await arionpay.VerifyWebhookAsync(Request.Body, Request.Headers["ArionPay-Signature"])</c>.
    /// </summary>
    public async Task<WebhookEvent> VerifyWebhookAsync(Stream body, string? signatureHeader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var ms = new MemoryStream();
        var buffer = new byte[16384];
        int n;
        while ((n = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > 1 << 20) throw new ArionPaySignatureException("The webhook body is too large.");
            ms.Write(buffer, 0, n);
        }
        return VerifyWebhook(ms.ToArray(), signatureHeader);
    }

    /// <summary>Never shows the secrets.</summary>
    public override string ToString() => $"ArionPayClient({_origin}{_basePath}, key={_apiKey})";

    /// <summary>Disposes the HttpClient this client made (not one you passed in).</summary>
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    // ---------------------------------------------------------------------------------------------------- transport

    private static void CheckId(string invoiceId)
    {
        if (invoiceId is null || !Id.IsMatch(invoiceId))
            throw new ArgumentException("Invoice id must be the 24-character id ArionPay returned.", nameof(invoiceId));
    }

    private async Task<JsonElement> RequestAsync(string method, string path, Dictionary<string, object?>? body, bool retrySafe, bool signed, CancellationToken ct)
    {
        var raw = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body);
        var replayRetryUsed = false;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SendAsync(method, path, raw, signed, ct).ConfigureAwait(false);
            }
            catch (ArionPayApiException e)
            {
                // Two identical signed requests in the same second have the same signature; only the first is accepted.
                if (e.Status == 401 && e.Message.Contains("already used", StringComparison.OrdinalIgnoreCase) && !replayRetryUsed)
                {
                    replayRetryUsed = true;
                    attempt--;
                    await Delay(TimeSpan.FromMilliseconds(1100), ct).ConfigureAwait(false);
                    continue;
                }
                var retryAfter = e.RetryAfterSeconds ?? 0;
                var temporary = e.Status is 0 or 502 or 503 or 504 || (e.Status == 429 && retryAfter <= 5);
                if (!retrySafe || !temporary || attempt >= _maxAttempts) throw;
                var wait = e.Status == 429 ? TimeSpan.FromSeconds(Math.Max(1, retryAfter)) : TimeSpan.FromMilliseconds(500 * Math.Pow(3, attempt - 1));
                await Delay(wait, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<JsonElement> SendAsync(string method, string path, byte[]? raw, bool signed, CancellationToken ct)
    {
        var q = path.IndexOf('?');
        var fullPath = _basePath + (q < 0 ? path : path[..q] + "?" + path[(q + 1)..]);
        using var req = new HttpRequestMessage(new HttpMethod(method), new Uri(_origin + fullPath));
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (signed)
        {
            var ts = Clock();
            req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            req.Headers.TryAddWithoutValidation("x-timestamp", ts.ToString(CultureInfo.InvariantCulture));
            req.Headers.TryAddWithoutValidation("x-signature", ArionPayWebhook.SignRequest(_apiSecret, ts, method, fullPath, raw));
        }
        if (raw is not null)
        {
            req.Content = new ByteArrayContent(raw);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        HttpResponseMessage res;
        byte[] data;
        try
        {
            res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            data = await res.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new ArionPayApiException($"Could not reach ArionPay: no answer within {_timeout.TotalSeconds:0} seconds.", 0, "network_error", null, null, e);
        }
        catch (HttpRequestException e)
        {
            throw new ArionPayApiException("Could not reach ArionPay: " + e.Message, 0, "network_error", null, null, e);
        }

        using (res)
        {
            var obj = EmptyObject;
            if (data.Length > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object) obj = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // an HTML error page from a proxy: keep the HTTP status
                }
            }
            var status = (int)res.StatusCode;
            if (status >= 200 && status < 300) return obj;

            var message = Invoice.Str(obj, "error") is { Length: > 0 } m ? m : Invoice.Str(obj, "message") is { Length: > 0 } m2 ? m2 : "HTTP " + status;
            int? retryAfter = null;
            if (obj.TryGetProperty("retryAfterSec", out var ra) && ra.ValueKind == JsonValueKind.Number && ra.TryGetInt32(out var rav)) retryAfter = rav;
            if (res.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero) retryAfter = (int)delta.TotalSeconds;
            throw new ArionPayApiException($"ArionPay API error ({status}): {message}", status, Invoice.Str(obj, "code"),
                data.Length > 0 ? Encoding.UTF8.GetString(data) : null, retryAfter);
        }
    }
}
