using MyOwnLLM.Cli;

namespace MyOwnLLM.Gateway.Tests;

public class ConsoleTextTests
{
    [Fact]
    public void Sanitize_strips_escape_and_control_chars_but_keeps_newlines_and_tabs()
    {
        var hostile = "ok\e]0;pwned\a\e[31mred\u009b2J\r\nnext\tline\0";

        Assert.Equal("ok]0;pwned[31mred2J\nnext\tline", ConsoleText.Sanitize(hostile));
    }

    [Fact]
    public void Sanitize_keeps_normal_unicode()
    {
        Assert.Equal("café · ✓", ConsoleText.Sanitize("café · ✓"));
    }

    [Fact]
    public void Sanitize_null_is_empty()
    {
        Assert.Equal("", ConsoleText.Sanitize(null));
    }
}
