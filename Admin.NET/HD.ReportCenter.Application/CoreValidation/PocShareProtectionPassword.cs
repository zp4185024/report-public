using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;

namespace HD.ReportCenter.Application.CoreValidation;

internal sealed record PocStoredShareProtection(
    bool Enabled,
    string Salt,
    string Hash,
    int Iterations);

internal static class PocShareProtectionPassword
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 120_000;
    private const string PropertyName = "ShareProtection";

    public static PocStoredShareProtection Create(string password, bool enabled)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(password, salt, Iterations);
        return new PocStoredShareProtection(
            enabled,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash),
            Iterations);
    }

    public static bool Verify(PocStoredShareProtection stored, string password)
    {
        try
        {
            var salt = Convert.FromBase64String(stored.Salt);
            var expected = Convert.FromBase64String(stored.Hash);
            if (salt.Length != SaltSize || expected.Length != HashSize || stored.Iterations <= 0)
            {
                return false;
            }

            var actual = Derive(password, salt, stored.Iterations);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static PocStoredShareProtection? Read(string? previewSettingsJson)
    {
        if (string.IsNullOrWhiteSpace(previewSettingsJson))
        {
            return null;
        }

        var root = JObject.Parse(previewSettingsJson);
        var token = root.GetValue(PropertyName, StringComparison.OrdinalIgnoreCase);
        return token?.ToObject<PocStoredShareProtection>();
    }

    public static string Write(
        PocPreviewSettings preview,
        PocStoredShareProtection? protection)
    {
        var root = JObject.FromObject(preview);
        if (protection is not null)
        {
            root[PropertyName] = JToken.FromObject(protection);
        }

        return root.ToString(Formatting.None);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashSize);
}
