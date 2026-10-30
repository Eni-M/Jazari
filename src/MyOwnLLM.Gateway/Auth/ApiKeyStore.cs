using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyOwnLLM.Gateway.Auth;

/// <summary>Personal API keys, stored only as SHA-256 hashes.</summary>
public sealed class ApiKeyStore(string keyFilePath)
{
    sealed record KeyFile(List<string> Hashes);

    public string AddKey()
    {
        var key = "mollm_" + Base64Url(RandomNumberGenerator.GetBytes(32));
        var hashes = Load();
        hashes.Add(Hash(key));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyFilePath))!);
        File.WriteAllText(keyFilePath, JsonSerializer.Serialize(new KeyFile(hashes), JsonSerializerOptions.Web));
        return key;
    }

    public bool IsValid(string? presentedKey)
    {
        if (string.IsNullOrEmpty(presentedKey)) return false;
        var presented = Convert.FromHexString(Hash(presentedKey));
        return Load().Any(h => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(h), presented));
    }

    List<string> Load() =>
        File.Exists(keyFilePath)
            ? JsonSerializer.Deserialize<KeyFile>(File.ReadAllText(keyFilePath), JsonSerializerOptions.Web)?.Hashes ?? []
            : [];

    static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
