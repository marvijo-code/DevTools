namespace DevTools.Infrastructure;

public static class ByteFormatter
{
    public static string Format(long bytes)
    {
        const double scale = 1024d;
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var suffixIndex = 0;
        double displayValue = bytes;
        while (displayValue >= scale && suffixIndex < suffixes.Length - 1)
        {
            displayValue /= scale;
            suffixIndex++;
        }

        return suffixIndex == 0 ? $"{displayValue:0} {suffixes[suffixIndex]}" : $"{displayValue:0.00} {suffixes[suffixIndex]}";
    }
}
