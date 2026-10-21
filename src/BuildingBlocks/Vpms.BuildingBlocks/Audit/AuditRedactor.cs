namespace Vpms.BuildingBlocks.Audit;

/// <summary>Keeps sensitive values out of audit payloads and API responses.</summary>
public static class AuditRedactor
{
    /// <summary>"DE89370400440532013000" becomes "••••3000". Short values are fully masked.</summary>
    public static string MaskTail(string? value, int keep = 4)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var visible = value.Length > keep * 2 ? value[^keep..] : "";
        return "••••" + visible;
    }
}
