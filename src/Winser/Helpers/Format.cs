namespace Winser.Helpers;

public static class Format
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long value)
    {
        if (value <= 0)
        {
            return "0 B";
        }

        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < ByteUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:N0} {ByteUnits[unit]}"
            : $"{size:0.#} {ByteUnits[unit]}";
    }

    /// <summary>"Just now", "12 minutes ago", "Yesterday, 14:03", "3 Mar 2026, 09:11".</summary>
    public static string RelativeTime(DateTimeOffset utc)
    {
        var local = utc.ToLocalTime();
        var delta = DateTimeOffset.Now - local;

        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta < TimeSpan.FromMinutes(1))
        {
            return "Just now";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            var minutes = (int)delta.TotalMinutes;
            return $"{minutes} minute{(minutes == 1 ? string.Empty : "s")} ago";
        }

        if (local.Date == DateTimeOffset.Now.Date)
        {
            return local.ToString("HH:mm");
        }

        if (local.Date == DateTimeOffset.Now.Date.AddDays(-1))
        {
            return $"Yesterday, {local:HH:mm}";
        }

        return local.Year == DateTimeOffset.Now.Year
            ? local.ToString("d MMM, HH:mm")
            : local.ToString("d MMM yyyy, HH:mm");
    }

    /// <summary>Bucket header used by the history list: "Today", "Yesterday", or a date.</summary>
    public static string DayBucket(DateTimeOffset utc)
    {
        var date = utc.ToLocalTime().Date;
        var today = DateTime.Today;

        if (date == today)
        {
            return "Today";
        }

        if (date == today.AddDays(-1))
        {
            return "Yesterday";
        }

        return date.Year == today.Year
            ? date.ToString("dddd, d MMMM")
            : date.ToString("d MMMM yyyy");
    }
}
