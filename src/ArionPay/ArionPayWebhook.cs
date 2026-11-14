using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArionPay;

/// <summary>A verified ArionPay webhook.</summary>
public sealed class WebhookEvent
{
    /// <summary>Event id ("evt_..."): handle each one once.</summary>
    public string Id { get; init; } = "";
    /// <summary>invoice.pending, invoice.partial, invoice.paid, invoice.expired or invoice.needs_review.</summary>
    public string Type { get; init; } = "";
    /// <summary>Unix seconds.</summary>
    public long Created { get; init; }
    /// <summary>Sent by a live store.</summary>
    public bool Livemode { get; init; }
    /// <summary>Set on events made by "Send test webhook"; see <see cref="ArionPayWebhook.IsTestEvent"/>.</summary>
    public bool Test { get; init; }
    /// <summary>The invoice this event is about.</summary>
    public Invoice Invoice { get; init; } = null!;
    /// <summary>The whole event as ArionPay sent it.</summary>
    public JsonElement Raw { get; init; }
}

/// <summary>Webhook and request signatures.</summary>
public static class ArionPayWebhook
{
    /// <summary>Webhooks signed more than this many seconds ago (or ahead) are refused. ArionPay signs again on every retry.</summary>
    public const long DefaultToleranceSeconds = 300;

    /// <summary>The webhook event types ArionPay sends.</summary>
    public static readonly IReadOnlyList<string> EventTypes = new[] { "invoice.pending", "invoice.partial", "invoice.paid", "invoice.expired", "invoice.needs_review" };

    internal static string HmacHex(string secret, byte[] data) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data)).ToLowerInvariant();

    private static byte[] Concat(string prefix, ReadOnlySpan<byte> body)
    {
        var p = Encoding.UTF8.GetBytes(prefix);
        var o = new byte[p.Length + body.Length];
        p.CopyTo(o, 0);
        body.CopyTo(o.AsSpan(p.Length));
        return o;
    }

    /// <summary>
    /// The x-signature of one API request. POST: hex HMAC-SHA256(secret, "&lt;ts&gt;.&lt;raw body&gt;"),
    /// GET: hex HMAC-SHA256(secret, "&lt;ts&gt;.GET &lt;path and query&gt;").
    /// </summary>
    public static string SignRequest(string secretKey, long timestamp, string method, string pathAndQuery, byte[]? rawBody)
    {
        var ts = timestamp.ToString(CultureInfo.InvariantCulture);
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            return HmacHex(secretKey, Encoding.UTF8.GetBytes(ts + ".GET " + pathAndQuery));
        return HmacHex(secretKey, Concat(ts + ".", rawBody ?? Array.Empty<byte>()));
    }

    /// <summary>Reads "t=&lt;unix seconds&gt;,v1=&lt;hex&gt;". False when the header is malformed.</summary>
    public static bool TryParseSignatureHeader(string? header, out long timestamp, out string v1)
    {
        timestamp = 0;
        v1 = "";
        if (header is null) return false;
        string? t = null, sig = null;
        foreach (var part in header.Split(','))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var k = part[..eq].Trim();
            var v = part[(eq + 1)..].Trim();
            if (k == "t") t = v;
            else if (k == "v1") sig = v;
        }
        if (t is null || sig is null || t.Length is < 1 or > 12 || sig.Length != 64) return false;
        foreach (var c in t) if (c is < '0' or > '9') return false;
        foreach (var c in sig) if (!Uri.IsHexDigit(c)) return false;
        timestamp = long.Parse(t, CultureInfo.InvariantCulture);
        v1 = sig.ToLowerInvariant();
        return true;
    }

    /// <summary>True when header is a valid, fresh signature of exactly rawBody. Never throws.</summary>
    public static bool IsValidSignature(byte[]? rawBody, string? header, string? webhookSecret, long toleranceSeconds, long nowUnix)
    {
        if (rawBody is null || string.IsNullOrEmpty(webhookSecret)) return false;
        if (!TryParseSignatureHeader(header, out var t, out var v1)) return false;
        if (Math.Abs(nowUnix - t) > toleranceSeconds) return false;
        var expected = HmacHex(webhookSecret, Concat(t.ToString(CultureInfo.InvariantCulture) + ".", rawBody));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(v1));
    }

    /// <summary>
    /// Checks a webhook and returns the event, or throws <see cref="ArionPaySignatureException"/> (answer 401).
    /// </summary>
    /// <param name="rawBody">The exact bytes ArionPay sent; never parsed and re-encoded JSON.</param>
    /// <param name="signatureHeader">The ArionPay-Signature header.</param>
    /// <param name="webhookSecret">Your webhook signing secret (whsec_...).</param>
    /// <param name="toleranceSeconds">Clock window (default 300).</param>
    /// <param name="nowUnix">The current time (default: now).</param>
    public static WebhookEvent Verify(byte[]? rawBody, string? signatureHeader, string? webhookSecret, long toleranceSeconds = DefaultToleranceSeconds, long? nowUnix = null)
    {
        if (string.IsNullOrEmpty(webhookSecret))
            throw new ArionPaySignatureException("The webhook secret is empty. Copy it from the Webhooks page of your ArionPay dashboard (it starts with whsec_).");
        if (rawBody is null)
            throw new ArionPaySignatureException("Pass the raw request body (the exact bytes ArionPay sent).");
        if (!TryParseSignatureHeader(signatureHeader, out _, out _))
            throw new ArionPaySignatureException("Missing or malformed ArionPay-Signature header.");
        if (!IsValidSignature(rawBody, signatureHeader, webhookSecret, toleranceSeconds, nowUnix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            throw new ArionPaySignatureException("Invalid webhook signature or stale timestamp. Check the webhook secret and your server clock.");
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ArionPaySignatureException("The webhook body is not JSON.");
        }
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString())
            || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(type.GetString())
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("invoice", out var inv) || inv.ValueKind != JsonValueKind.Object)
            throw new ArionPaySignatureException("The webhook body is not an ArionPay event.");
        return new WebhookEvent
        {
            Id = id.GetString()!,
            Type = type.GetString()!,
            Created = root.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var cv) ? cv : 0,
            Livemode = Invoice.Flag(root, "livemode"),
            Test = Invoice.Flag(root, "test"),
            Invoice = Invoice.FromWebhook(inv),
            Raw = root,
        };
    }

    /// <summary><see cref="Verify(byte[], string, string, long, long?)"/> for a body you already hold as text (signed as UTF-8).</summary>
    public static WebhookEvent Verify(string? rawBody, string? signatureHeader, string? webhookSecret) =>
        Verify(rawBody is null ? null : Encoding.UTF8.GetBytes(rawBody), signatureHeader, webhookSecret);

    /// <summary>True for events made by "Send test webhook": a made-up invoice that must never change an order.</summary>
    public static bool IsTestEvent(WebhookEvent ev) =>
        ev.Test || ev.Id.StartsWith("evt_test_", StringComparison.Ordinal) || ev.Invoice.Id.StartsWith("inv_test_", StringComparison.Ordinal);
}
