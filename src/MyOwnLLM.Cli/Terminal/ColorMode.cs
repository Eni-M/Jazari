namespace MyOwnLLM.Cli.Terminal;

public enum ColorMode { None, Ansi16, Ansi256, TrueColor }

public static class ColorModeDetector
{
    /// <summary>NO_COLOR or redirected output → none; truecolor terminals (COLORTERM, Windows Terminal) → 24-bit; TERM *256* → 256; else 16.</summary>
    public static ColorMode Detect(Func<string, string?> getEnv, bool outputRedirected)
    {
        if (outputRedirected || !string.IsNullOrEmpty(getEnv("NO_COLOR"))) return ColorMode.None;
        if (getEnv("COLORTERM") is "truecolor" or "24bit" || !string.IsNullOrEmpty(getEnv("WT_SESSION"))) return ColorMode.TrueColor;
        if (getEnv("TERM")?.Contains("256") == true) return ColorMode.Ansi256;
        return ColorMode.Ansi16;
    }
}
