using System.Security.Cryptography;
using System.Text;

namespace VisionIQ.Api.Logging;

/// <summary>
/// Turns a user's sign-in name into a stable pseudonym for logs ("u-" + 16 hex). Keyed (HMAC-SHA256 with
/// LOG_PSEUDONYM_KEY) because e-mail addresses are guessable and a plain hash could be reversed by trying them.
/// The same person always gets the same pseudonym; only holders of the key can link it back to a person.
/// </summary>
public sealed class IdentityPseudonymizer
{
    private readonly byte[] key;

    public IdentityPseudonymizer(string? configuredKey)
    {
        IsKeyed = !string.IsNullOrWhiteSpace(configuredKey) && configuredKey.Length >= 32;
        // Without a key the pseudonym is still stable but only as strong as an unkeyed hash (startup warns).
        key = Encoding.UTF8.GetBytes(IsKeyed ? configuredKey! : "visioniq-log-pseudonym-unkeyed-v1");
    }

    /// <summary>True when a proper secret key (32+ characters) is configured.</summary>
    public bool IsKeyed { get; }

    public string For(string? principal)
    {
        if (string.IsNullOrWhiteSpace(principal))
        {
            return "u-anonymous";
        }

        var digest = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(principal.Trim().ToLowerInvariant()));
        return "u-" + Convert.ToHexStringLower(digest)[..16];
    }
}
