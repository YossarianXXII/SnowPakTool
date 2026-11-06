namespace SnowPakTool.Desktop;

internal static class Program
{
    private static readonly string StartupLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnowPakTool.Desktop",
        "startup-error.txt");

    [STAThread]
    private static void Main()
    {
        try
        {
            WriteStartupStage("Main entered");
            ApplicationConfiguration.Initialize();
            WriteStartupStage("WinForms initialized");
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, eventArgs) => ReportStartupError(eventArgs.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            {
                if (eventArgs.ExceptionObject is Exception exception) ReportStartupError(exception);
            };

            using var context = new WorkspaceApplicationContext();
            WriteStartupStage("Main window created");
            Application.Run(context);
        }
        catch (Exception exception)
        {
            ReportStartupError(exception);
        }
    }

    private sealed class WorkspaceApplicationContext : ApplicationContext
    {
        public WorkspaceApplicationContext() => Open();
        private void Open()
        {
            var form = new MainForm(); MainForm = form;
            form.ReloadRequested += () =>
            {
                // Swap the application-context window before closing the old one.
                // This reloads all planning stores without retaining stale editor state.
                Open(); form.Close(); form.Dispose();
            };
            form.Show();
        }
    }

    private static void WriteStartupStage(string stage)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartupLogPath)!);
            File.AppendAllText(StartupLogPath, $"{DateTimeOffset.Now:O} {stage}{Environment.NewLine}");
        }
        catch
        {
            // Keep diagnostics from causing another startup failure.
        }
    }

    private static void ReportStartupError(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartupLogPath)!);
            File.AppendAllText(StartupLogPath, $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Keep error reporting from throwing while handling the original startup failure.
        }

        try
        {
            MessageBox.Show(
                $"SnowPakTool could not start or encountered an unhandled error.{Environment.NewLine}{Environment.NewLine}{exception.Message}{Environment.NewLine}{Environment.NewLine}Details: {StartupLogPath}",
                "SnowPakTool startup error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // The error log remains available if Windows cannot display a dialog.
        }
    }
}
