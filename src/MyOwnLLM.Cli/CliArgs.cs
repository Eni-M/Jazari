namespace MyOwnLLM.Cli;

public sealed record CliArgs(string Command, string? Text, string? RepoPath, bool Stream, string? Id, bool? Accepted)
{
    public const string Usage =
        """
        usage:
          jazari                          open a conversation in this folder
          jazari ask "<question>"         one question; pipe text in: git diff | jazari ask "review this"
          jazari diff "<question>"        one question about your uncommitted changes
          jazari commit                   a commit message for the staged changes, copied for git commit
          jazari review [path]            review a whole project and write a report
          jazari review --changed         review only what changed since the last commit
          jazari status                   where we left off
          jazari feedback <ix-id> up|down rate an answer
          jazari tune                     measure the fastest settings for this PC (a few minutes)
          jazari stop                     stop the background servers to free memory
          jazari update                   update Jazari
          jazari doctor                   check the install, servers, key and logs, and say what to fix
          jazari --version
        options for ask, diff and status: --repo <path>   ask and diff: --no-stream
        before any command: --plain       plain lines without colors or symbols, for screen readers (or JAZARI_PLAIN=1)
        """;

    public static CliArgs Parse(string[] args)
    {
        switch (args)
        {
            case []:
                return new("session", null, null, true, null, null);
            case ["help"] or ["--help"] or ["-h"] or ["/?"]:
                return new("help", null, null, true, null, null);
            case ["--version"] or ["version"]:
                return new("version", null, null, true, null, null);
            case ["commit"]:
                return new("commit", null, null, true, null, null);
            case ["diff", var question, .. var rest] when !question.StartsWith("--"):
                var (diffRepo, diffStream) = Options(rest, allowStream: true);
                return new("diff", question, diffRepo, diffStream, null, null);
            case ["tune"]:
                return new("tune", null, null, true, null, null);
            case ["stop"]:
                return new("stop", null, null, true, null, null);
            case ["doctor"]:
                return new("doctor", null, null, true, null, null);
            case ["review"]:
                return new("review", null, null, true, null, null);
            case ["review", "--changed"]:
                return new("review", "--changed", null, true, null, null);
            case ["review", var path] when !path.StartsWith("--"):
                return new("review", path, null, true, null, null);
            case ["ask", var text, .. var rest] when !text.StartsWith("--"):
                var (repo, stream) = Options(rest, allowStream: true);
                return new("ask", text, repo, stream, null, null);
            case ["status", .. var rest]:
                return new("status", null, Options(rest, allowStream: false).Repo, true, null, null);
            case ["feedback", var id, "up"]:
                return new("feedback", null, null, true, id, true);
            case ["feedback", var id, "down"]:
                return new("feedback", null, null, true, id, false);
            default:
                throw new ArgumentException(Usage);
        }
    }

    static (string? Repo, bool Stream) Options(string[] rest, bool allowStream)
    {
        string? repo = null;
        var stream = true;
        for (var i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--repo" when i + 1 < rest.Length: repo = rest[++i]; break;
                case "--stream" when allowStream: stream = true; break;
                case "--no-stream" when allowStream: stream = false; break;
                default: throw new ArgumentException(Usage);
            }
        }
        return (repo, stream);
    }
}
