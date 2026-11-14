using System.Collections.Generic;

namespace ArionPay;

/// <summary>What to put on a new invoice. Only <see cref="Amount"/> is required; null fields are not sent.</summary>
public sealed class InvoiceRequest
{
    /// <summary>The price, e.g. 49.90m.</summary>
    public decimal Amount { get; set; }
    /// <summary>Price currency (default USD): USD, EUR, GBP, CAD, AUD, CHF, JPY, TRY, AED, INR, BRL, ...</summary>
    public string Currency { get; set; } = "USD";
    /// <summary>Your order number; comes back as <see cref="Invoice.OrderId"/>.</summary>
    public string? OrderId { get; set; }
    /// <summary>Shown on the payment page.</summary>
    public string? Description { get; set; }
    /// <summary>The customer's email, for ArionPay's receipt.</summary>
    public string? CustomerEmail { get; set; }
    /// <summary>Where the customer goes after paying. Coming back here does NOT prove payment.</summary>
    public string? SuccessUrl { get; set; }
    /// <summary>Where the customer goes when they give up.</summary>
    public string? CancelUrl { get; set; }
    /// <summary>Where ArionPay sends this invoice's webhooks (default: your store's webhook address).</summary>
    public string? CallbackUrl { get; set; }
    /// <summary>Pre-select a coin, e.g. "USDT_TRC20".</summary>
    public string? Asset { get; set; }
    /// <summary>Coins the customer may choose from.</summary>
    public IReadOnlyList<string>? AllowedAssets { get; set; }
    /// <summary>Minutes until the invoice expires (5 to 1440).</summary>
    public int? ExpiresMin { get; set; }
    /// <summary>Your data (a dictionary or an anonymous object); sent back in every webhook of this invoice.</summary>
    public object? Metadata { get; set; }
    /// <summary>The same key always returns the same invoice (a random one is used when you set none).</summary>
    public string? IdempotencyKey { get; set; }
    /// <summary>true for a test invoice (no real money).</summary>
    public bool? Test { get; set; }
}
