using System.Text;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace MyOwnLLM.Gateway.Tests;

/// <summary>
/// Replays what the real jazari wrote to a pseudo-terminal, phase by phase (files named "NN-phase-WIDTHxHEIGHT.bin"),
/// through the terminal emulator, and checks nothing wrapped on its own, no resize made the terminal reflow a row, and every synchronized update was closed. Only
/// runs when JAZARI_PTY_CAPTURE names a folder of captures; each screen is then also written there as HTML.
/// </summary>
public class CaptureReplayTests(ITestOutputHelper output)
{
    [Fact]
    public void A_captured_session_draws_cleanly_at_every_size()
    {
        if (Environment.GetEnvironmentVariable("JAZARI_PTY_CAPTURE") is not { Length: > 0 } dir) return;
        VtScreen? vt = null;
        foreach (var file in Directory.GetFiles(dir, "*.bin").Order())
        {
            var size = Regex.Match(Path.GetFileName(file), @"(\d+)x(\d+)");
            if (size.Success)
            {
                var (width, height) = (int.Parse(size.Groups[1].Value), int.Parse(size.Groups[2].Value));
                if (vt is null) vt = new VtScreen(width, height);
                else if (vt.Width != width || vt.Height != height) vt.Resize(width, height);
            }
            vt!.Write(Encoding.UTF8.GetString(File.ReadAllBytes(file)));
            output.WriteLine($"==== {Path.GetFileName(file)}: soft wraps {vt.SoftWraps}, rows the terminal reflowed {vt.Reflowed}, sync depth {vt.SyncDepth}, sync error {vt.SyncError}");
            foreach (var line in vt.Lines) output.WriteLine("|" + line);
            File.WriteAllText(Path.ChangeExtension(file, ".html"), vt.ToHtml());
        }
        Assert.NotNull(vt);
        Assert.Equal(0, vt!.SoftWraps);
        Assert.Equal(0, vt.Reflowed);
        Assert.False(vt.SyncError);
    }
}
