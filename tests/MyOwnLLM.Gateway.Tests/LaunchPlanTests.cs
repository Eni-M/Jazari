using Jazari.Launcher;

namespace MyOwnLLM.Gateway.Tests;

public class LaunchPlanTests
{
    const string Config = """{"installDir":"C:\\J","sourceDir":"C:\\J\\source","repoUrl":"https://github.com/Eni-M/MyOwnLLM.git"}""";
    static bool All(string _) => true;

    [Fact]
    public void Runs_the_app_with_arguments_passed_through()
    {
        var plan = LaunchPlan.Decide(["ask", "hi", "--repo", @"C:\my repo"], Config, All);

        Assert.Equal("run", plan.Kind);
        Assert.Equal(@"C:\J\app\jazari.exe", plan.Exe);
        Assert.Null(plan.WorkingDirectory); // the app runs in the user's folder: that folder is the project
        Assert.Equal(["ask", "hi", "--repo", @"C:\my repo"], plan.Args);
    }

    [Fact]
    public void No_arguments_runs_the_app()
    {
        var plan = LaunchPlan.Decide([], Config, All);

        Assert.Equal("run", plan.Kind);
        Assert.Empty(plan.Args);
    }

    [Fact]
    public void Update_prefers_the_updater_the_last_good_update_kept()
    {
        var plan = LaunchPlan.Decide(["update"], Config, All);

        Assert.Equal(@"C:\J\update-jazari.ps1", plan.Args[^1]);
    }

    [Fact]
    public void Update_runs_the_updater()
    {
        var plan = LaunchPlan.Decide(["update"], Config, path => path != @"C:\J\update-jazari.ps1");

        Assert.Equal("update", plan.Kind);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), plan.Exe);
        Assert.Equal(@"C:\J\source", plan.WorkingDirectory);
        Assert.Equal(["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @"C:\J\source\scripts\update-jazari.ps1"], plan.Args);
    }

    [Theory]
    [InlineData(0, true)]  // updated
    [InlineData(1, true)]  // failed: the previous version is still there
    [InlineData(2, false)] // the user answered "n"
    [InlineData(-1073741510, false)] // Ctrl+C
    public void Jazari_reopens_unless_the_update_was_stopped(int updaterExit, bool reopens) =>
        Assert.Equal(reopens, LaunchPlan.ReopensAfterUpdate(updaterExit));

    [Theory]
    [InlineData(null)]
    [InlineData("{not json")]
    [InlineData("""{"home":"C:\\R"}""")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"installDir":1,"sourceDir":"C:\\S"}""")]
    public void Missing_or_old_config_says_to_install(string? config)
    {
        var plan = LaunchPlan.Decide([], config, All);

        Assert.Equal("fail", plan.Kind);
        Assert.Contains("install-jazari.ps1", plan.Message);
    }

    [Fact]
    public void Missing_app_says_to_update()
    {
        var plan = LaunchPlan.Decide([], Config, path => !path.EndsWith(@"app\jazari.exe"));

        Assert.Equal("fail", plan.Kind);
        Assert.Equal("Jazari isn't fully installed. Run: jazari update", plan.Message);
    }

    [Fact]
    public void Missing_updater_says_to_reinstall()
    {
        var plan = LaunchPlan.Decide(["update"], Config, path => !path.EndsWith("update-jazari.ps1"));

        Assert.Equal("fail", plan.Kind);
        Assert.Contains("install-jazari.ps1 again", plan.Message);
    }
}
