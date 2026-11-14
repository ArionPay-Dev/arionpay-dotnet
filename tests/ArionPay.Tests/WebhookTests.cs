using System;
using System.Text;
using Xunit;

namespace ArionPay.Tests;

public class WebhookTests
{
    internal const string Secret = "whsec_unit_0001";
    internal const string Event = "{\"id\":\"evt_abc\",\"type\":\"invoice.paid\",\"created\":1,\"livemode\":true,\"data\":{\"invoice\":"
        + "{\"id\":\"6af75d7ce83f10cb386f0981\",\"status\":\"paid\",\"orderId\":\"1001\",\"amountFiat\":49.9,\"currency\":\"USD\","
        + "\"asset\":\"USDT_TRC20\",\"paidCrypto\":\"49.900001\",\"test\":false,\"metadata\":{\"user\":42},\"transactions\":[]}}}";

    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    internal static string Signed(byte[] body, long t, string secret = Secret)
    {
        var prefix = Encoding.UTF8.GetBytes(t + ".");
        var all = new byte[prefix.Length + body.Length];
        prefix.CopyTo(all, 0);
        body.CopyTo(all, prefix.Length);
        return $"t={t},v1={ArionPayWebhook.HmacHex(secret, all)}";
    }

    internal static string Signed(string body) => Signed(Encoding.UTF8.GetBytes(body), Now());

    [Fact]
    public void RequestSignaturesMatchBackend()
    {
        // reference values computed independently with python3 hmac
        Assert.Equal("e6855fc36dc68ee45a227f7bfff6965585803c9530238b1b771746c118a91770",
            ArionPayWebhook.SignRequest("sk_x", 1700000000, "POST", "/api/v1/invoices", Encoding.UTF8.GetBytes("{\"a\":1}")));
        Assert.Equal("76c4e503c0883422e6c050aef0becffba060d8814f3a8f95c604dc1914639ef2",
            ArionPayWebhook.SignRequest("sk_x", 1700000000, "get", "/api/v1/invoices/abc?x=1", Encoding.UTF8.GetBytes("ignored")));
    }

    [Fact]
    public void HeaderParsing()
    {
        var v1 = new string('a', 64);
        Assert.True(ArionPayWebhook.TryParseSignatureHeader("t=123,v1=" + v1, out var t, out var s));
        Assert.Equal(123, t);
        Assert.Equal(v1, s);
        Assert.True(ArionPayWebhook.TryParseSignatureHeader(" v1=" + v1.ToUpperInvariant() + " , t=5 ", out t, out s));
        Assert.Equal(5, t);
        Assert.Equal(v1, s);
        foreach (var bad in new[] { "", null, "t=1", "v1=" + v1, "t=x,v1=" + v1, "t=1,v1=zz", "t=1,v1=" + v1 + "0", "t=-1,v1=" + v1, "t=1,v1=" + new string('g', 64) })
            Assert.False(ArionPayWebhook.TryParseSignatureHeader(bad, out _, out _), bad);
    }

    [Fact]
    public void ValidEvent()
    {
        var ev = ArionPayWebhook.Verify(Event, Signed(Event), Secret);
        Assert.Equal("evt_abc", ev.Id);
        Assert.Equal("invoice.paid", ev.Type);
        Assert.True(ev.Livemode);
        Assert.Equal(1, ev.Created);
        var inv = ev.Invoice;
        Assert.Equal("6af75d7ce83f10cb386f0981", inv.Id);
        Assert.True(inv.IsPaid);
        Assert.Equal("1001", inv.OrderId);
        Assert.Equal(49.9m, inv.AmountFiat);
        Assert.Equal("49.900001", inv.PaidCrypto);
        Assert.Equal(42, inv.Metadata.GetProperty("user").GetInt32());
        Assert.True(inv.Matches(49.90m, "usd"));
        Assert.False(inv.Matches(49.91m, "USD"));
        Assert.False(inv.Matches(49.90m, "EUR"));
        Assert.False(ArionPayWebhook.IsTestEvent(ev));
        Assert.True(ArionPayWebhook.IsValidSignature(Encoding.UTF8.GetBytes(Event), Signed(Event), Secret, 300, Now()));
    }

    [Fact]
    public void NonAscii()
    {
        var body = Event.Replace("\"status\":\"paid\"", "\"status\":\"paid\",\"description\":\"Café ☕ – 東京 \\u00e9\"");
        var ev = ArionPayWebhook.Verify(body, Signed(body), Secret);
        Assert.Equal("Café ☕ – 東京 é", ev.Invoice.Raw.GetProperty("description").GetString());
    }

    [Fact]
    public void Failures()
    {
        var hdr = Signed(Event);
        Assert.Throws<ArionPaySignatureException>(() => ArionPayWebhook.Verify(Event.Replace("paid", "PAID"), hdr, Secret));
        Assert.Throws<ArionPaySignatureException>(() => ArionPayWebhook.Verify(" " + Event, hdr, Secret));
        Assert.Throws<ArionPaySignatureException>(() => ArionPayWebhook.Verify(Event, hdr, "whsec_other"));
        Assert.Contains("empty", Assert.Throws<ArionPaySignatureException>(() => ArionPayWebhook.Verify(Event, hdr, "")).Message);
        Assert.Contains("empty", Assert.Throws<ArionPaySignatureException>(() => ArionPayWebhook.Verify(Event, hdr, null)).Message);
        var body = Encoding.UTF8.GetBytes(Event);
        var old = Now() - 301;
        Assert.Contains("stale", Assert.Throws<ArionPaySignatureException>(() => ArionPayWebhook.Verify(body, Signed(body, old), Secret)).Message);
        Assert.Contains("stale", Assert.Throws<ArionPaySignatureException>(() => ArionPayWebhook.Verify(body, Signed(body, old + 602), Secret)).Message);
        Assert.True(ArionPayWebhook.IsValidSignature(body, Signed(body, old), Secret, 600, Now()));
        Assert.Contains("Missing or malformed", Assert.Throws<ArionPaySignatureException>(() => ArionPayWebhook.Verify(body, null, Secret)).Message);
        Assert.Contains("raw request body", Assert.Throws<ArionPaySignatureException>(() => ArionPayWebhook.Verify((byte[]?)null, hdr, Secret)).Message);
        Assert.False(ArionPayWebhook.IsValidSignature(body, hdr, "", 300, Now()));
        Assert.False(ArionPayWebhook.IsValidSignature(null, hdr, Secret, 300, Now()));
    }

    [Fact]
    public void SignedButNotEvent()
    {
        foreach (var b in new[] { "not json", "{}", "[]", "{\"id\":\"\",\"type\":\"invoice.paid\",\"data\":{\"invoice\":{}}}",
                     "{\"id\":\"evt_1\",\"type\":\"invoice.paid\"}", "{\"id\":\"evt_1\",\"type\":\"invoice.paid\",\"data\":{\"invoice\":null}}",
                     "{\"id\":5,\"type\":\"invoice.paid\",\"data\":{\"invoice\":{}}}", "{\"id\":\"evt_1\",\"type\":\"invoice.paid\",\"data\":{\"invoice\":{}}} x" })
            Assert.Throws<ArionPaySignatureException>(() => ArionPayWebhook.Verify(b, Signed(b), Secret));
        var notUtf8 = new byte[] { 0xff, 0xfe, (byte)'{', (byte)'}' };
        Assert.Throws<ArionPaySignatureException>(() => ArionPayWebhook.Verify(notUtf8, Signed(notUtf8, Now()), Secret));
    }

    [Fact]
    public void OddFieldTypesStillVerify()
    {
        var b = "{\"id\":\"evt_1\",\"type\":\"invoice.paid\",\"data\":{\"invoice\":{\"id\":\"6af75d7ce83f10cb386f0981\",\"status\":\"PAID\","
                + "\"amountFiat\":\"12.5\",\"currency\":\"USD\",\"metadata\":[1],\"transactions\":{\"x\":1},\"late\":\"yes\",\"paidAt\":null,\"orderId\":77,\"customerEmail\":\"a@b.c\"}}}";
        var inv = ArionPayWebhook.Verify(b, Signed(b), Secret).Invoice;
        Assert.Equal("77", inv.OrderId);
        Assert.Equal("paid", inv.Status);
        Assert.True(inv.Matches(12.50m, "USD"));
        Assert.Equal(System.Text.Json.JsonValueKind.Object, inv.Metadata.ValueKind);
        Assert.Empty(inv.Metadata.EnumerateObject());
        Assert.Empty(inv.Transactions);
        Assert.False(inv.Late);
        Assert.Equal("", inv.PaidAt);
        Assert.Equal("a@b.c", inv.Raw.GetProperty("customerEmail").GetString());
    }

    [Fact]
    public void TestEvents()
    {
        var b1 = Event.Replace("\"livemode\":true", "\"livemode\":false,\"test\":true");
        Assert.True(ArionPayWebhook.IsTestEvent(ArionPayWebhook.Verify(b1, Signed(b1), Secret)));
        var b2 = Event.Replace("evt_abc", "evt_test_1");
        Assert.True(ArionPayWebhook.IsTestEvent(ArionPayWebhook.Verify(b2, Signed(b2), Secret)));
        var b3 = Event.Replace("6af75d7ce83f10cb386f0981", "inv_test_x");
        Assert.True(ArionPayWebhook.IsTestEvent(ArionPayWebhook.Verify(b3, Signed(b3), Secret)));
    }
}
