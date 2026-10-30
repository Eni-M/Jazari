using MyOwnLLM.Gateway.Auth;

namespace MyOwnLLM.Gateway.Tests;

public class ApiKeyStoreTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("mollm-keys").FullName;
    string KeyFile => Path.Combine(_dir, "keys.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void AddKey_returns_prefixed_key_and_stores_only_hash()
    {
        var store = new ApiKeyStore(KeyFile);

        var key = store.AddKey();

        Assert.StartsWith("mollm_", key);
        Assert.DoesNotContain(key, File.ReadAllText(KeyFile));
        Assert.True(store.IsValid(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mollm_wrong")]
    public void IsValid_rejects_null_empty_and_wrong_keys(string? presented)
    {
        var store = new ApiKeyStore(KeyFile);
        store.AddKey();

        Assert.False(store.IsValid(presented));
    }

    [Fact]
    public void Keys_survive_reload()
    {
        var key = new ApiKeyStore(KeyFile).AddKey();

        Assert.True(new ApiKeyStore(KeyFile).IsValid(key));
    }

    [Fact]
    public void Missing_file_means_no_valid_keys()
    {
        Assert.False(new ApiKeyStore(KeyFile).IsValid("mollm_anything"));
    }
}
