using System.Diagnostics;
using MyOwnLLM.Gateway.State;
using MyOwnLLM.Gateway.Storage;

namespace MyOwnLLM.Gateway.Tests;

public class ProjectStateTests : IDisposable
{

    [Fact]
    public void A_project_is_a_folder_inside_a_repository()
    {
        var root = Directory.CreateTempSubdirectory("jazari repo").FullName;
        try
        {
            var inner = Directory.CreateDirectory(Path.Combine(root, "src", "deep")).FullName;
            Assert.False(GitSummary.IsRepo(inner));

            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Assert.True(GitSummary.IsRepo(inner));

            Directory.Delete(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: elsewhere"); // a linked worktree
            Assert.True(GitSummary.IsRepo(inner));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    readonly string _dir = Directory.CreateTempSubdirectory("mollm-state").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    ProjectStateStore NewStore() => new(new LocalDb(Path.Combine(_dir, "test.db")));

    [Fact]
    public async Task State_set_get_roundtrip_and_truncates_to_3200_chars()
    {
        var store = NewStore();

        await store.SetAsync(@"C:\Repo", "short note");
        Assert.Equal("short note", await store.GetAsync(@"C:\Repo"));

        await store.SetAsync(@"C:\Repo", new string('n', 5000));
        Assert.Equal(3200, (await store.GetAsync(@"C:\Repo"))!.Length);
    }

    [Fact]
    public async Task State_key_ignores_case_and_trailing_slash()
    {
        var store = NewStore();

        await store.SetAsync(@"C:\Repo\", "note");

        Assert.Equal("note", await store.GetAsync(@"c:\repo"));
        Assert.Null(await store.GetAsync(@"C:\Other"));
    }

    [Fact]
    public async Task GitSummary_returns_empty_for_non_repo()
    {
        var plain = Directory.CreateDirectory(Path.Combine(_dir, "plain")).FullName;

        Assert.Equal("", await GitSummary.GetAsync(plain, CancellationToken.None));
    }

    [Fact]
    public async Task GitSummary_handles_path_with_spaces()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_dir, "my repo")).FullName;
        Git(repo, "init", "-q");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "a");
        Git(repo, "add", ".");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "first commit subject");

        var summary = await GitSummary.GetAsync(repo, CancellationToken.None);

        Assert.Contains("first commit subject", summary);
    }

    static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}
