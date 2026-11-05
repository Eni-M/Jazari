using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public class FixTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("mollm-fix").FullName;
    readonly FakeGateway _gateway = new();
    readonly StringWriter _output = new();
    readonly List<string> _asked = [];
    string? _lastContext;
    Func<string, bool> _confirm = _ => true;

    public FixTests()
    {
        _gateway.Complete = (_, context) => { _lastContext = context; return Task.FromResult(Edit); };
        File.WriteAllText(Path.Combine(_dir, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(A, "class A { int B() => 1; }\n");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    const string Edit = """{"edits":[{"search":"=> 1","replace":"=> 2"}]}""";
    string A => Path.Combine(_dir, "A.cs");

    Session NewSession()
    {
        var theme = new Theme(ColorMode.None, unicode: true);
        return new Session(_gateway, new Ui(_output, theme, 80), () => new MarkdownStream(_output, theme, 80), _dir)
        {
            Confirm = q => { _asked.Add(q); return _confirm(q); },
            TrustFile = Path.Combine(_dir, "trust.json"),
            ProjectErrors = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<string>?>([]),
        };
    }

    [Fact] public async Task Extra_context_reaches_the_model_after_the_file()
    {
        await NewSession().ProposeEditAsync(A, "A.cs", "change it", "EXTRA", false, false, default);
        Assert.Contains("--- A.cs ---", _lastContext);
        Assert.EndsWith("EXTRA", _lastContext);
        Assert.Contains("=> 2", File.ReadAllText(A));
    }

    [Fact] public async Task Saying_no_to_the_edit_returns_false_and_writes_nothing()
    {
        _confirm = _ => false;
        Assert.False(await NewSession().ProposeEditAsync(A, "A.cs", "change it", null, false, false, default));
        Assert.Contains("=> 1", File.ReadAllText(A));
    }
}
