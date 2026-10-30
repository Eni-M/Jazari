namespace MyOwnLLM.Cli;

public sealed record CliArgs(string Command, string? Text, string? RepoPath, bool Stream, string? Id, bool? Accepted)
{
    public const string Usage =
        "usage: myllm ask \"<question>\" [--repo <path>] [--stream] | myllm status [--repo <path>] | myllm feedback <ix-id> up|down";

    public static CliArgs Parse(string[] args)
    {
        switch (args)
        {
            case ["ask", var text, .. var rest] when !text.StartsWith("--"):
                var (repo, stream) = Options(rest, allowStream: true);
                return new("ask", text, repo, stream, null, null);
            case ["status", .. var rest]:
                return new("status", null, Options(rest, allowStream: false).Repo, false, null, null);
            case ["feedback", var id, "up"]:
                return new("feedback", null, null, false, id, true);
            case ["feedback", var id, "down"]:
                return new("feedback", null, null, false, id, false);
            default:
                throw new ArgumentException(Usage);
        }
    }

    static (string? Repo, bool Stream) Options(string[] rest, bool allowStream)
    {
        string? repo = null;
        var stream = false;
        for (var i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--repo" when i + 1 < rest.Length: repo = rest[++i]; break;
                case "--stream" when allowStream: stream = true; break;
                default: throw new ArgumentException(Usage);
            }
        }
        return (repo, stream);
    }
}
