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
    /// <summary>The key jazari starts llama-server with (ServerLauncher.LlamaKeyFile, the same file by default).</summary>
    public string LlamaKeyFile => Path.Combine(DataDir, "llama-server.key");

    /// <summary>The law book, made by lawbook/build.py and installed by jazari update once scripts/jazari-manifest.json names it.
    /// Missing means Jazari answers without it.</summary>
    public string LawBookFile { get => _lawBookFile ?? Path.Combine(DataDir, "law-book.db"); set => _lawBookFile = value; }
    string? _lawBookFile;

    /// <summary>The law book search's two small models, each in its own folder as in spike/models: bge-small-en-v1.5 (vectors)
    /// and ms-marco-MiniLM-L12-v2 (reranker). Missing means the word search alone.</summary>
    public string SearchModelsDir { get => _searchModelsDir ?? Path.Combine(DataDir, "search-models"); set => _searchModelsDir = value; }
    string? _searchModelsDir;
}
