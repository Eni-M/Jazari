using MyOwnLLM.Cli;

namespace MyOwnLLM.Gateway.Tests;

public class JazariConfigTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("jazari-config").FullName;
    string File => Path.Combine(_dir, "sub", "config.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Save_then_load_roundtrips()
    {
        var config = new JazariConfig(@"C:\Repos\MyOwnLLM", @"C:\x\MyOwnLLM.Gateway.exe", @"C:\x\llama-server.exe", @"C:\x\granite.gguf");

        config.Save(File);

        Assert.Equal(config, JazariConfig.Load(File));
    }

    [Fact]
    public void Old_config_without_new_fields_still_loads()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, """{"home":"C:\\R","gatewayExe":"g.exe","llamaServer":"l.exe","model":"m.gguf"}""");

        var config = JazariConfig.Load(File)!;

        Assert.Equal("g.exe", config.GatewayExe);
        Assert.Null(config.RepoUrl);
        Assert.Null(config.InstalledCommit);
    }

    [Fact]
    public void New_fields_roundtrip()
    {
        var config = new JazariConfig(@"C:\R", "g.exe", "l.exe", "m.gguf", "https://github.com/Eni-M/MyOwnLLM.git", @"C:\J", @"C:\J\source", new string('a', 40));

        config.Save(File);

        Assert.Equal(config, JazariConfig.Load(File));
        Assert.Contains("\"installedCommit\"", System.IO.File.ReadAllText(File));
    }

    [Fact]
    public void Missing_file_loads_as_null() => Assert.Null(JazariConfig.Load(File));

    [Fact]
    public void Corrupt_file_loads_as_null()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, "{not json");

        Assert.Null(JazariConfig.Load(File));
    }

    [Fact]
    public void A_file_that_cant_be_read_loads_as_null()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, "{}");
        using var held = new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.None); // an update writing it, say

        Assert.Null(JazariConfig.Load(File));
    }

    [Fact]
    public void Stop_command_parses() => Assert.Equal("stop", CliArgs.Parse(["stop"]).Command);
}
