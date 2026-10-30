using MyOwnLLM.Cli;

namespace MyOwnLLM.Gateway.Tests;

public class SettingsTests
{
    static Func<string, string?> Env(params (string Name, string Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void Jazari_key_wins_over_old_name()
    {
        var s = Settings.FromEnvironment(Env(("JAZARI_KEY", "new"), ("MYOWNLLM_KEY", "old")));

        Assert.Equal("new", s.Key);
    }

    [Fact]
    public void Old_key_is_a_fallback()
    {
        Assert.Equal("old", Settings.FromEnvironment(Env(("MYOWNLLM_KEY", "old"))).Key);
    }

    [Fact]
    public void Url_defaults_to_local_gateway()
    {
        var s = Settings.FromEnvironment(Env());

        Assert.Null(s.Key);
        Assert.Equal(new Uri("http://127.0.0.1:5080"), s.GatewayUrl);
    }

    [Fact]
    public void Jazari_url_is_respected()
    {
        Assert.Equal(new Uri("http://127.0.0.1:6000"), Settings.FromEnvironment(Env(("JAZARI_URL", "http://127.0.0.1:6000"))).GatewayUrl);
    }
}
