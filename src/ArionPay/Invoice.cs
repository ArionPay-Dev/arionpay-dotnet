using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ArionPay;

/// <summary>
/// An invoice with the field names of the webhook's <c>data.invoice</c>: the same class for webhooks
/// (<see cref="WebhookEvent.Invoice"/>) and signed reads (<see cref="ArionPayClient.GetInvoiceAsync"/>).
/// Crypto amounts are kept as text, exactly as ArionPay sent them. <see cref="Raw"/> has every field.
/// </summary>
public sealed class Invoice
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>24-character invoice id (or "inv_test_..." in a sample event).</summary>
    public string Id { get; private init; } = "";
    /// <summary>new, pending, paid, expired, ... (lower case).</summary>
    public string Status { get; private init; } = "";
    /// <summary>True when ArionPay counts this invoice as fully paid.</summary>
    public bool IsPaid => Status == "paid";
    /// <summary>Your order id ("" when none).</summary>
    public string OrderId { get; private init; } = "";
    /// <summary>The invoice price in <see cref="Currency"/>; null when missing.</summary>
    public decimal? AmountFiat { get; private init; }
    /// <summary>Price currency, e.g. "USD".</summary>
    public string Currency { get; private init; } = "";
    /// <summary>The coin, e.g. "USDT_TRC20" ("" before one is chosen).</summary>
    public string Asset { get; private init; } = "";
    /// <summary>Blockchain network of the coin.</summary>
    public string Network { get; private init; } = "";
    /// <summary>Crypto amount asked for.</summary>
    public string AmountCrypto { get; private init; } = "";
    /// <summary>Crypto amount received so far.</summary>
    public string PaidCrypto { get; private init; } = "";
    /// <summary>Crypto amount with enough confirmations.</summary>
    public string ConfirmedCrypto { get; private init; } = "";
    /// <summary>More than the price arrived.</summary>
    public bool Overpaid { get; private init; }
    /// <summary>Paid slightly under, inside the store's payment tolerance (still paid).</summary>
    public bool UnderpaidWithinTolerance { get; private init; }
    /// <summary>Paid after the invoice expired.</summary>
    public bool Late { get; private init; }
    /// <summary>Something needs a person (Payment Radar).</summary>
    public bool NeedsReview { get; private init; }
    /// <summary>Why it needs review.</summary>
    public string ReviewReason { get; private init; } = "";
    /// <summary>The merchant accepted this payment by hand in the dashboard.</summary>
    public bool Manual { get; private init; }
    /// <summary>A test invoice (no real money).</summary>
    public bool Test { get; private init; }
    /// <summary>Blockchain transactions seen for this invoice.</summary>
    public IReadOnlyList<JsonElement> Transactions { get; private init; } = Array.Empty<JsonElement>();
    /// <summary>Your data from <see cref="InvoiceRequest.Metadata"/> (a JSON object, empty when none).</summary>
    public JsonElement Metadata { get; private init; } = EmptyObject;
    /// <summary>When it was paid (ISO 8601), "" when not paid.</summary>
    public string PaidAt { get; private init; } = "";
    /// <summary>Transaction hash, when known.</summary>
    public string TxHash { get; private init; } = "";
    /// <summary>Every field as ArionPay sent it (also customerEmail, createdAt, expiresAt, confirmations, ...).</summary>
    public JsonElement Raw { get; private init; } = EmptyObject;

    /// <summary>
    /// True when the invoice price is exactly this order total (to the cent) in this currency: the check to make before
    /// you deliver. Crypto amounts never need comparing: ArionPay decides whether an invoice is paid.
    /// </summary>
    public bool Matches(decimal orderTotal, string orderCurrency)
    {
        if (AmountFiat is null || orderCurrency is null) return false;
        return Math.Round(AmountFiat.Value, 2, MidpointRounding.AwayFromZero) == Math.Round(orderTotal, 2, MidpointRounding.AwayFromZero)
               && string.Equals(Currency, orderCurrency, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A short description for logs.</summary>
    public override string ToString() => $"Invoice {Id} {Status} {AmountFiat?.ToString(CultureInfo.InvariantCulture)} {Currency} {Asset}{(Test ? " (test)" : "")}";

    // ------------------------------------------------------------------------------------------------- reading

    internal static string Str(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    internal static bool Flag(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    internal static decimal? Dec(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }

    private static JsonElement Obj(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v.Clone() : EmptyObject;

    private static IReadOnlyList<JsonElement> Arr(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<JsonElement>();
        var list = new List<JsonElement>();
        foreach (var x in v.EnumerateArray()) list.Add(x.Clone());
        return list;
    }

    private static string Or(string a, string b) => a.Length > 0 ? a : b;

    /// <summary>From a webhook's data.invoice. Odd field types become empty, never errors.</summary>
    internal static Invoice FromWebhook(JsonElement m) => new()
    {
        Raw = m.Clone(),
        Id = Str(m, "id"),
        Status = Str(m, "status").ToLowerInvariant(),
        OrderId = Str(m, "orderId"),
        AmountFiat = Dec(m, "amountFiat"),
        Currency = Str(m, "currency"),
        Asset = Str(m, "asset"),
        Network = Str(m, "network"),
        AmountCrypto = Str(m, "amountCrypto"),
        PaidCrypto = Str(m, "paidCrypto"),
        ConfirmedCrypto = Str(m, "confirmedCrypto"),
        Overpaid = Flag(m, "overpaid"),
        UnderpaidWithinTolerance = Flag(m, "underpaidWithinTolerance"),
        Late = Flag(m, "late"),
        NeedsReview = Flag(m, "needsReview"),
        ReviewReason = Str(m, "reviewReason"),
        Manual = Flag(m, "manual"),
        Test = Flag(m, "test"),
        Transactions = Arr(m, "transactions"),
        Metadata = Obj(m, "metadata"),
        PaidAt = Str(m, "paidAt"),
        TxHash = Str(m, "txHash"),
    };

    /// <summary>From the signed read (GET /api/v1/invoices/&lt;id&gt;), renamed to the webhook field names.</summary>
    internal static Invoice FromRead(JsonElement r) => new()
    {
        Raw = r.Clone(),
        Id = Or(Str(r, "invoiceId"), Str(r, "id")),
        Status = Str(r, "status").ToLowerInvariant(),
        OrderId = Str(r, "orderId"),
        AmountFiat = Dec(r, "amountFiat"),
        Currency = Str(r, "currency"),
        Asset = Str(r, "settledCurrency"),
        Network = Str(r, "network"),
        AmountCrypto = Str(r, "amountExpected"),
        PaidCrypto = Or(Str(r, "amountReceived"), "0"),
        ConfirmedCrypto = Or(Str(r, "amountConfirmed"), "0"),
        Overpaid = Flag(r, "overpaid"),
        UnderpaidWithinTolerance = Flag(r, "underpaidWithinTolerance"),
        Late = Flag(r, "late"),
        NeedsReview = Flag(r, "needsReview"),
        ReviewReason = Str(r, "reviewReason"),
        Manual = Flag(r, "manual"),
        Test = Flag(r, "test"),
        Transactions = Arr(r, "transactions"),
        Metadata = Obj(r, "metadata"),
        PaidAt = Str(r, "settledAt"),
        TxHash = Str(r, "txHash"),
    };
}

/// <summary>The answer of <see cref="ArionPayClient.CreateInvoiceAsync"/>. Send the customer to <see cref="InvoiceUrl"/>.</summary>
public sealed class CreatedInvoice
{
    /// <summary>24-character invoice id: keep it with your order.</summary>
    public string Id { get; init; } = "";
    /// <summary>The payment page to send the customer to.</summary>
    public string InvoiceUrl { get; init; } = "";
    /// <summary>"new" for a fresh invoice.</summary>
    public string Status { get; init; } = "";
    /// <summary>The price.</summary>
    public decimal? AmountFiat { get; init; }
    /// <summary>Price currency.</summary>
    public string Currency { get; init; } = "";
    /// <summary>The coin when one was pre-selected ("" otherwise).</summary>
    public string Asset { get; init; } = "";
    /// <summary>Payment address when a coin was pre-selected.</summary>
    public string Address { get; init; } = "";
    /// <summary>Crypto amount when a coin was pre-selected.</summary>
    public string AmountCrypto { get; init; } = "";
    /// <summary>When the invoice expires (ISO 8601).</summary>
    public string ExpiresAt { get; init; } = "";
    /// <summary>Your order id.</summary>
    public string OrderId { get; init; } = "";
    /// <summary>The whole answer as ArionPay sent it.</summary>
    public JsonElement Raw { get; init; }
}
