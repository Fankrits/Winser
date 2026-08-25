namespace Winser.Models;

public sealed class SessionTab
{
    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
}

/// <summary>What gets written on shutdown so "restore previous session" can put it all back.</summary>
public sealed class SessionState
{
    public List<SessionTab> Tabs { get; set; } = [];

    public int SelectedIndex { get; set; }

    public int WindowWidth { get; set; }

    public int WindowHeight { get; set; }

    public int WindowLeft { get; set; } = int.MinValue;

    public int WindowTop { get; set; } = int.MinValue;

    public bool IsMaximized { get; set; }
}
