namespace SnowPakTool.Desktop;

internal sealed record RandomizationLogEntry(string Message, bool IsDetail = false);

internal sealed class RandomizationLogProgress(Action<RandomizationLogEntry> report) : IProgress<RandomizationLogEntry>
{
    public void Report(RandomizationLogEntry value) => report(value);
}
