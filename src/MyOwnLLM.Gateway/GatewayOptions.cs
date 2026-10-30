namespace MyOwnLLM.Gateway;

public sealed class GatewayOptions
{
    public string LlamaUrl { get; set; } = "http://127.0.0.1:8080";
    public string DataDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyOwnLLM");
    public int ContextTokens { get; set; } = 16384;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public string KeyFile => Path.Combine(DataDir, "keys.json");
    public string DbFile => Path.Combine(DataDir, "myownllm.db");
}
