using System;

namespace ArionPay;

/// <summary>Base class of every error this library throws (bad arguments throw <see cref="ArgumentException"/>).</summary>
public class ArionPayException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ArionPayException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>ArionPay answered with an error, or could not be reached (<see cref="Status"/> 0). Never contains your secret.</summary>
public sealed class ArionPayApiException : ArionPayException
{
    /// <summary>Creates the exception.</summary>
    public ArionPayApiException(string message, int status, string code, string? responseBody, int? retryAfterSeconds, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
        Code = code;
        ResponseBody = responseBody;
        RetryAfterSeconds = retryAfterSeconds;
    }

    /// <summary>HTTP status, 0 when ArionPay could not be reached.</summary>
    public int Status { get; }

    /// <summary>ArionPay error code, e.g. "idempotency_key_reused", "rate_limited", "unknown_asset" ("" when none).</summary>
    public string Code { get; }

    /// <summary>The error body as ArionPay sent it, when there was one.</summary>
    public string? ResponseBody { get; }

    /// <summary>Seconds to wait before trying again (from retryAfterSec or a Retry-After header), when given.</summary>
    public int? RetryAfterSeconds { get; }
}

/// <summary>A webhook must not be trusted: bad or stale signature, wrong secret, or not an ArionPay event. Answer 401.</summary>
public sealed class ArionPaySignatureException : ArionPayException
{
    /// <summary>Creates the exception.</summary>
    public ArionPaySignatureException(string message) : base(message) { }
}
