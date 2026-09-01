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

    /// <summary>构建 PocShareProtectionPassword 所需的 Create 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="password">password 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="enabled">enabled 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>校验 PocShareProtectionPassword 的 Verify 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="stored">stored 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="password">password 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>查询或读取 PocShareProtectionPassword 所需的 Read 数据，并按调用约定返回结果。</summary>
    /// <param name="previewSettingsJson">previewSettingsJson 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>更新 PocShareProtectionPassword 的 Write 状态，并同步相关业务数据。</summary>
    /// <param name="preview">preview 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="protection">protection 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>负责 PocShareProtectionPassword 的 Derive 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="password">password 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="salt">salt 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="iterations">iterations 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashSize);
}
