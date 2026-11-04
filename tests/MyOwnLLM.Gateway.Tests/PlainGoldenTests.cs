using System.Runtime.CompilerServices;
using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

/// <summary>
/// Pins piped (no color) output byte for byte, so reworking the live terminal can't change what scripts and pipes see.
/// Run with JAZARI_UPDATE_GOLDEN=1 to rewrite the files under Golden/ after an intended change.
/// </summary>
public class PlainGoldenTests
{
    static readonly Theme Plain = new(ColorMode.None, unicode: true);

    static string GoldenDir([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Golden");

    static void Check(string name, string actual)
    {
        actual = System.Text.RegularExpressions.Regex.Replace(actual.Replace("\r\n", "\n"), @"\d+\.\d s\b", "N s");
        var path = Path.Combine(GoldenDir(), name + ".txt");
        if (Environment.GetEnvironmentVariable("JAZARI_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(GoldenDir());
            File.WriteAllText(path, actual);
            return;
        }
        Assert.True(File.Exists(path), $"missing golden file {path}; run with JAZARI_UPDATE_GOLDEN=1");
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), actual); // checked out with CRLF on Windows
    }

    [Fact]
    public void Ui_components()
    {
        var output = new StringWriter();
        var ui = new Ui(output, Plain, 80);
        ui.Header("~/MyOwnLLM", "main", "0.3.0");
        ui.Wordmark("0.3.0");
        ui.Blank();
        ui.Row("why", "A long explanation that has to wrap because it is wider than the eighty columns this test gives it to use.");
        ui.Line("a line");
        ui.Label("models");
        ui.Rows([("Model", "Granite 4.1 3B · local"), ("Directory", "~/EduCore"), ("A very long key name", "value")]);
        ui.Error("Can't reach the gateway.", "Nothing answered.", "jazari update");
        ui.UserMessage("what is dependency injection?\nand a second line");
        ui.Metadata("2 files, 2.1k tokens.");
        ui.DiffLine("18", "readonly Channel<Work> _work =", Tone.Muted, Surface.Code);
        ui.DiffLine("-", "    Channel.CreateUnbounded<Work>();", Tone.Muted, Surface.Removed);
        ui.DiffLine("+", "    Channel.CreateBounded<Work>(32);", Tone.Primary, Surface.Added);
        ui.TuneProgress(["cpu 4 threads", "pick fastest"], 1, "about 2 min left", redraw: false);
        Check("ui-components", output.ToString());
    }

    const string Corpus = """
        # A heading
        Some text with **bold**, `inline code` and a [link](https://learn.microsoft.com/dotnet).
        - first bullet that is long enough to wrap around onto a second row when the width is small
        - second
        1. numbered
        12. another numbered item

        ```csharp
        builder.Services.AddScoped<IOrderRepository, OrderRepository>();
            indented();
        ```
        ```
        no info string
        ```
        | Lifetime | Instances |
        |---|---|
        | Transient | every time |
        | Scoped | per request |
        after the table
        averyveryveryverylongwordwithoutanyspacesthatmustbesplitbecauseitislongerthantherowitself
        """;

    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    public void Markdown_corpus(int width)
    {
        var output = new StringWriter();
        var stream = new MarkdownStream(output, Plain, width);
        foreach (var chunk in Chunk(Corpus, 7)) stream.Write(chunk);
        stream.Complete();
        Check($"markdown-{width}", output.ToString());
    }

    static IEnumerable<string> Chunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size) yield return text.Substring(i, Math.Min(size, text.Length - i));
    }

    [Fact]
    public async Task Session_flows()
    {
        var output = new StringWriter();
        var gateway = new FakeGateway
        {
            Chat = (_, question, _) => question.Contains("code")
                ? FakeGateway.Chunks("Use this:\n```csharp\nvar x = 1;\n```\nand then:\n```\ny();\n```\n")
                : FakeGateway.Chunks("A class gets the things it needs ", "handed to it."),
        };
        var dir = Directory.CreateTempSubdirectory("jazari golden").FullName;
        try
        {
            var session = new Session(gateway, new Ui(output, Plain, 80), () => new MarkdownStream(output, Plain, 80), dir);
            await session.HandleAsync("what is dependency injection?", CancellationToken.None);
            await session.HandleAsync("show code", CancellationToken.None);
            await session.HandleAsync("/help", CancellationToken.None);
            await session.HandleAsync("/copy 9", CancellationToken.None);
            Check("session-flows", output.ToString().Replace(dir, "<dir>"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
