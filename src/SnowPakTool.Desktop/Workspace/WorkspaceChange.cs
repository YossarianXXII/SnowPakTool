namespace SnowPakTool.Desktop;

internal sealed record WorkspaceChange(string State, string Target, string Property, string Before, string After, string Key = "",
    string SummaryGroup = "", string SummaryName = "")
{
    public const string InFiles = "In files";
    public const string Pending = "Pending";
}
