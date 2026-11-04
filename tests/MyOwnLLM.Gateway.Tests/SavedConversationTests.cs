using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;
using MyOwnLLM.Gateway.State;

namespace MyOwnLLM.Gateway.Tests;

/// <summary>Leaving Jazari and coming back in the same folder carries on the conversation.</summary>
public sealed class SavedConversationTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "jazari-conversations-" + Guid.NewGuid().ToString("N"));
    readonly FakeGateway _gateway = new();
    readonly StringWriter _output = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    Session Open(string project = @"C:\Repo", string? dir = "")
    {
        var theme = new Theme(ColorMode.None, unicode: true);
        return new Session(_gateway, new Ui(_output, theme, 80), () => new MarkdownStream(_output, theme, 80), project) { ConversationDir = dir == "" ? _dir : dir };
    }

    Session Reopen(string project = @"C:\Repo")
    {
        var session = Open(project);
        session.Resume();
        return session;
    }

    [Fact]
    public async Task A_follow_up_after_reopening_goes_with_the_previous_question_and_answer()
    {
        _gateway.Chat = (_, _, _) => FakeGateway.Chunks("Records are ", "immutable.\n```cs\nrecord R;\n```");
        await Open().HandleAsync("what is a récord?", CancellationToken.None);

        var reopened = Reopen();
        await reopened.HandleAsync("elaborate", CancellationToken.None);

        var (history, question) = _gateway.Asked[^1];
        Assert.Equal("elaborate", question);
        // Exactly the same text in the same order, or the prompt cache can't reuse what it already read.
        Assert.Equal([new ChatTurn("user", "what is a récord?"), new ChatTurn("assistant", "Records are immutable.\n```cs\nrecord R;\n```")], history);
    }

    [Fact]
    public async Task Reopening_says_what_it_carried_on_from()
    {
        await Open().AskAsync("one", CancellationToken.None);

        Reopen();

        Assert.Contains("Carrying on from your last conversation (1 question). /clear starts over.", _output.ToString());
    }

    [Fact]
    public async Task Clear_starts_over_for_good()
    {
        var session = Open();
        await session.AskAsync("one", CancellationToken.None);

        await session.HandleAsync("/clear", CancellationToken.None);
        var reopened = Reopen();

        Assert.Empty(reopened.History);
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.DoesNotContain("Carrying on", _output.ToString());
    }

    [Fact]
    public async Task Clear_with_nothing_saved_yet_is_quiet()
    {
        await Open().HandleAsync("/clear", CancellationToken.None);

        Assert.DoesNotContain("Couldn't", _output.ToString());
    }

    [Fact]
    public async Task Each_project_has_its_own_conversation()
    {
        await Open(@"C:\One").AskAsync("about one", CancellationToken.None);

        Assert.Empty(Reopen(@"C:\Two").History);
        Assert.Equal("about one", Reopen(@"c:\one\").History[0].Content);
    }

    [Fact]
    public async Task The_same_note_stays_with_a_reopened_conversation_so_the_prompt_cache_holds()
    {
        var notes = new ConversationNotes();
        await Open().AskAsync("one", CancellationToken.None);
        notes.For(@"C:\Repo", newConversation: _gateway.Asked[0].History.Count == 0, "note when it began");

        var reopened = Reopen();
        await reopened.AskAsync("elaborate", CancellationToken.None);
        var note = notes.For(@"C:\Repo", newConversation: _gateway.Asked[^1].History.Count == 0, "note updated since");
        Assert.Equal("note when it began", note);

        await reopened.HandleAsync("/clear", CancellationToken.None);
        await reopened.AskAsync("fresh", CancellationToken.None);
        note = notes.For(@"C:\Repo", newConversation: _gateway.Asked[^1].History.Count == 0, "note updated since");
        Assert.Equal("note updated since", note);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("""[{"Role":"user","Content":"a question with no answer"}]""")]
    [InlineData("""[{"Role":"assistant","Content":"a"},{"Role":"user","Content":"b"}]""")]
    [InlineData("""[{"Role":"user"},{"Role":"assistant"}]""")]
    public async Task A_damaged_file_starts_fresh(string damaged)
    {
        await Open().AskAsync("one", CancellationToken.None);
        File.WriteAllText(Directory.GetFiles(_dir).Single(), damaged);

        var reopened = Reopen();

        Assert.Empty(reopened.History);
        Assert.DoesNotContain("Carrying on", _output.ToString());
    }

    [Fact]
    public async Task A_failed_save_keeps_the_answer_and_says_so_once()
    {
        File.WriteAllText(_dir, "a file where the folder should be");
        try
        {
            var session = Open();
            await session.AskAsync("one", CancellationToken.None);
            await session.AskAsync("two", CancellationToken.None);

            Assert.Equal(4, session.History.Count);
            var text = _output.ToString();
            Assert.Contains("Couldn't save", text);
            Assert.Equal(text.IndexOf("Couldn't save"), text.LastIndexOf("Couldn't save"));
        }
        finally { File.Delete(_dir); }
    }

    [Fact]
    public async Task One_off_commands_neither_read_nor_write_it()
    {
        await Open().AskAsync("one", CancellationToken.None);
        var saved = File.ReadAllText(Directory.GetFiles(_dir).Single());

        var oneOff = Open(dir: null);
        oneOff.Resume();
        await oneOff.AskAsync("from a script", CancellationToken.None);

        Assert.Equal("from a script", _gateway.Asked[^1].Question);
        Assert.Empty(_gateway.Asked[^1].History);
        Assert.Equal(saved, File.ReadAllText(Directory.GetFiles(_dir).Single()));
    }

    [Fact]
    public async Task Nothing_lands_in_the_project_and_the_file_name_does_not_show_the_folder()
    {
        var project = Path.Combine(_dir, "secret-client-name");
        Directory.CreateDirectory(project);

        await Open(project, Path.Combine(_dir, "saved")).AskAsync("one", CancellationToken.None);

        Assert.Empty(Directory.GetFileSystemEntries(project));
        Assert.DoesNotContain("secret-client-name", Directory.GetFiles(Path.Combine(_dir, "saved")).Single());
    }

    [Fact]
    public async Task A_question_typed_while_the_model_starts_still_goes_with_the_history()
    {
        await Open().AskAsync("one", CancellationToken.None);

        var theme = new Theme(ColorMode.TrueColor, unicode: true);
        var terminal = new FakeTerminal(80, 16);
        var screen = new Screen(terminal, theme, bottom: true, paced: false);
        var ui = new Ui(screen, theme, () => 79);
        var historyFile = Path.Combine(_dir, "prompt-history");
        var editor = new PromptEditor(ui, screen, terminal, Path.GetTempPath, historyFile);
        var session = new Session(_gateway, ui, () => new MarkdownStream(screen, theme), @"C:\Repo") { ConversationDir = _dir };
        var started = new TaskCompletionSource<(bool, BlockBuffer)>();

        var running = InputLoop.RunInteractiveAsync(session, ui, "0.2.0", @"C:\Repo", editor, () => started.Task, null, measured: true);
        terminal.Type("elaborate\n");
        await Task.Delay(50);
        Assert.Single(_gateway.Asked); // still waiting for the model, only the first session's question so far
        started.SetResult((true, new BlockBuffer()));
        for (var i = 0; i < 500 && _gateway.Asked.Count < 2; i++) await Task.Delay(10);
        terminal.Type("/exit\n");
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("elaborate", _gateway.Asked[^1].Question);
        Assert.Equal("one", _gateway.Asked[^1].History[0].Content);
    }

    [Fact]
    public async Task A_save_leaves_no_copies_behind()
    {
        await Open().AskAsync("one", CancellationToken.None);

        Assert.Single(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public async Task The_plain_session_carries_on()
    {
        await Open().AskAsync("one", CancellationToken.None);

        var theme = new Theme(ColorMode.None, unicode: true);
        var ui = new Ui(_output, theme, 80);
        var session = new Session(_gateway, ui, () => new MarkdownStream(_output, theme, 80), @"C:\Repo") { ConversationDir = _dir };
        await InputLoop.RunAsync(session, ui, "0.2.0", @"C:\Repo", new TextLineReader(new StringReader("elaborate\n")));

        Assert.Equal("one", _gateway.Asked[^1].History[0].Content);
    }
}
