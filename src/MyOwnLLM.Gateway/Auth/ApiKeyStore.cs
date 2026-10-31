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
        List<string> hashes;
        try { hashes = Load(); }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            // A damaged file holds no usable key: keep it aside for a look, and start again so the user isn't locked out.
            File.Copy(keyFilePath, keyFilePath + ".damaged", overwrite: true);
            hashes = [];
        }
        hashes.Add(Hash(key));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyFilePath))!);
        // Written beside it, then swapped in: the running gateway reads the file on every request and must never see half of it.
        var temp = keyFilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new KeyFile(hashes), JsonSerializerOptions.Web));
        File.Move(temp, keyFilePath, overwrite: true);
        return key;
    }

    public bool IsValid(string? presentedKey)
    {
        if (string.IsNullOrEmpty(presentedKey)) return false;
        var presented = Convert.FromHexString(Hash(presentedKey));
        for (var attempt = 1; ; attempt++)
        {
            try { return Load().Any(h => h.Length == 64 && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(h), presented)); }
            catch (IOException) when (attempt < 3) { Thread.Sleep(20); } // add-key swapping the file in
            catch (Exception e) when (e is JsonException or FormatException or IOException) { return false; } // damaged: add-key repairs it
        }
    }

    List<string> Load() =>
        File.Exists(keyFilePath)
            ? JsonSerializer.Deserialize<KeyFile>(File.ReadAllText(keyFilePath), JsonSerializerOptions.Web)?.Hashes ?? []
            : [];

    static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
