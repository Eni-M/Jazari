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
    public void Missing_file_loads_as_null() => Assert.Null(JazariConfig.Load(File));

    [Fact]
    public void Corrupt_file_loads_as_null()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, "{not json");

        Assert.Null(JazariConfig.Load(File));
    }

    [Fact]
    public void Stop_command_parses() => Assert.Equal("stop", CliArgs.Parse(["stop"]).Command);
}
