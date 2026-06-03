using System.Text.RegularExpressions;

namespace Lyrictified.Server;

public static class LyricOffsetHelper
{
    public static string GetOffsetFilePath(string originalPath)
    {
        var directory = Path.GetDirectoryName(originalPath)!;
        var fileName = Path.GetFileNameWithoutExtension(originalPath);
        var extension = Path.GetExtension(originalPath);
        return Path.Combine(directory, $"{fileName}.offset{extension}");
    }

    public static void SyncOffsetFile(string originalPath, double offsetSeconds)
    {
        var offsetPath = GetOffsetFilePath(originalPath);
        if (offsetSeconds == 0)
        {
            if (File.Exists(offsetPath))
            {
                File.Delete(offsetPath);
            }
            return;
        }

        var offsetMs = (int)(offsetSeconds * 1000);
        var content = File.ReadAllText(originalPath);
        var extension = Path.GetExtension(originalPath).TrimStart('.').ToLowerInvariant();
        var result = extension == "ttml" ? OffsetTtml(content, offsetMs) : OffsetLrc(content, offsetMs);
        File.WriteAllText(offsetPath, result);
    }

    private static string OffsetLrc(string content, int offsetMs)
    {
        return LrcTimestampRegex.Replace(content, match =>
        {
            var timeStr = match.Groups["time"].Value;
            var open = match.Groups["open"].Value;
            var close = match.Groups["close"].Value;
            var newTime = OffsetTime(timeStr, offsetMs);
            return $"{open}{newTime}{close}";
        });
    }

    private static readonly Regex LrcTimestampRegex = new(
        @"(?<open>[\[<])(?<time>\d{2}:\d{2}(?:\.\d{2,3})?)(?<close>[\]>])",
        RegexOptions.Compiled);

    private static string OffsetTime(string timeStr, int offsetMs)
    {
        var parts = timeStr.Split(':', '.');
        var minutes = int.Parse(parts[0]);
        var seconds = int.Parse(parts[1]);
        var fractionStr = parts.Length > 2 ? parts[2] : "";
        var fractionDigits = fractionStr.Length;

        int fractionMs = 0;
        if (fractionDigits == 2)
            fractionMs = int.Parse(fractionStr) * 10;
        else if (fractionDigits == 3)
            fractionMs = int.Parse(fractionStr);

        var totalMs = minutes * 60000 + seconds * 1000 + fractionMs + offsetMs;
        totalMs = Math.Max(0, totalMs);

        var newMinutes = totalMs / 60000;
        var newSeconds = (totalMs % 60000) / 1000;
        var newMs = totalMs % 1000;

        if (fractionDigits == 2)
        {
            var hundredths = newMs / 10;
            return $"{newMinutes:D2}:{newSeconds:D2}.{hundredths:D2}";
        }

        if (fractionDigits == 3)
        {
            return $"{newMinutes:D2}:{newSeconds:D2}.{newMs:D3}";
        }

        return $"{newMinutes:D2}:{newSeconds:D2}";
    }

    private static string OffsetTtml(string content, int offsetMs)
    {
        return TtmlTimeRegex.Replace(content, match =>
        {
            var attr = match.Groups["attr"].Value;
            var timeStr = match.Groups["time"].Value;
            var newTime = OffsetTtmlTime(timeStr, offsetMs);
            return $"{attr}=\"{newTime}\"";
        });
    }

    private static readonly Regex TtmlTimeRegex = new(
        @"(?<attr>\b(?:begin|end|dur)\s*)=\s*""(?<time>[\d:.]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string OffsetTtmlTime(string timeStr, int offsetMs)
    {
        var colonCount = timeStr.Count(c => c == ':');
        var hasDot = timeStr.Contains('.');
        var parts = timeStr.Split(':');

        int hours = 0, minutes = 0, seconds = 0, ms = 0;

        if (colonCount == 2)
        {
            hours = int.Parse(parts[0]);
            minutes = int.Parse(parts[1]);
            ParseSeconds(parts[2], out seconds, out ms);
        }
        else if (colonCount == 1)
        {
            minutes = int.Parse(parts[0]);
            ParseSeconds(parts[1], out seconds, out ms);
        }
        else
        {
            ParseSeconds(parts[0], out seconds, out ms);
        }

        var totalMs = hours * 3600000 + minutes * 60000 + seconds * 1000 + ms + offsetMs;
        totalMs = Math.Max(0, totalMs);

        var newHours = totalMs / 3600000;
        var newMinutes = (totalMs % 3600000) / 60000;
        var newSeconds = (totalMs % 60000) / 1000;
        var newMs = totalMs % 1000;

        if (colonCount == 2)
        {
            if (hasDot) return $"{newHours:D2}:{newMinutes:D2}:{newSeconds:D2}.{newMs:D3}";
            return $"{newHours:D2}:{newMinutes:D2}:{newSeconds:D2}";
        }

        if (colonCount == 1)
        {
            if (hasDot) return $"{newMinutes:D2}:{newSeconds:D2}.{newMs:D3}";
            return $"{newMinutes:D2}:{newSeconds:D2}";
        }

        if (hasDot) return $"{newSeconds:D2}.{newMs:D3}";
        return $"{newSeconds:D2}";
    }

    private static void ParseSeconds(string part, out int seconds, out int ms)
    {
        if (part.Contains('.'))
        {
            var secMs = part.Split('.');
            seconds = int.Parse(secMs[0]);
            var msStr = secMs[1].PadRight(3, '0')[..3];
            ms = int.Parse(msStr);
        }
        else
        {
            seconds = int.Parse(part);
            ms = 0;
        }
    }
}
