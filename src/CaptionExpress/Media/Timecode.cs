using System.Globalization;

namespace CaptionExpress.Media;

public static class Timecode
{
    public static string Format(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds:000}";
    }

    public static bool TryParse(string? value, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            return double.IsFinite(seconds) && seconds >= 0;

        var parts = value.Split(':');
        if (parts.Length is < 2 or > 3) return false;
        int hours = 0;
        int minutes;
        double secs;
        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out secs)) return false;
        }
        else
        {
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out minutes) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out secs)) return false;
        }
        if (hours < 0 || minutes < 0 || minutes >= 60 && parts.Length == 3 ||
            !double.IsFinite(secs) || secs < 0 || secs >= 60)
            return false;
        seconds = (double)hours * 3600 + (double)minutes * 60 + secs;
        return double.IsFinite(seconds);
    }
}
