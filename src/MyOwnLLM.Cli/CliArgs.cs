namespace MyOwnLLM.Cli;

public sealed record CliArgs(string Command, string? Text, string? RepoPath, bool Stream, string? Id, bool? Accepted)
{
    public const string Usage =
        "usage: jazari | jazari ask \"<question>\" [--repo <path>] [--no-stream] | jazari status [--repo <path>] | jazari feedback <ix-id> up|down | jazari stop";

    public static CliArgs Parse(string[] args)
    {
        switch (args)
        {
            case []:
                return new("session", null, null, true, null, null);
            case ["stop"]:
                return new("stop", null, null, true, null, null);
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
