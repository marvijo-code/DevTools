namespace DevTools.Models;

public enum ResourceAdviceSeverity
{
    Info,
    Warning,
    Critical
}

/// <summary>One actionable finding derived from the sampled history.</summary>
public sealed record ResourceAdvice(
    ResourceAdviceSeverity Severity,
    string Title,
    string Detail,
    string Action)
{
    public string SeverityGlyph => Severity switch
    {
        ResourceAdviceSeverity.Critical => "!!",
        ResourceAdviceSeverity.Warning => "!",
        _ => "i"
    };

    public string SeverityLabel => Severity switch
    {
        ResourceAdviceSeverity.Critical => "CRITICAL",
        ResourceAdviceSeverity.Warning => "WARNING",
        _ => "INFO"
    };
}
