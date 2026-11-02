namespace MyOwnLLM.Gateway;

public sealed class GatewayOptions
{
    public string LlamaUrl { get; set; } = "http://127.0.0.1:8080";
    public int Port { get; set; } = 5080; // eval/recall.py runs a second gateway on 5099 beside the installed one
    public string DataDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyOwnLLM");
    public int ContextTokens { get; set; } = 16384;
    public string ModelName { get; set; } = "Granite 4.0-H Tiny";
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(30); // a full context takes minutes to read, then minutes to answer

    public string KeyFile => Path.Combine(DataDir, "keys.json");
    public string DbFile => Path.Combine(DataDir, "myownllm.db");

    /// <summary>The law book, made by lawbook/build.py and installed by jazari update. Missing means Jazari answers without it.</summary>
    public string LawBookFile { get => _lawBookFile ?? Path.Combine(DataDir, "law-book.db"); set => _lawBookFile = value; }
    string? _lawBookFile;
}
