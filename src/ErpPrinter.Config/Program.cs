using ErpPrinter.Config.Model;
using ErpPrinter.Config.Ui;

namespace ErpPrinter.Config;

internal static class Program
{
    private const string Title = "ERP Printer";

    /// <summary>
    /// <c>ErpPrinterConfig.exe</c> runs against the machine (via <c>WindowsConfigActions</c>, see <see cref="Composition"/>);
    /// <c>--demo</c> runs against an in-memory configuration with fake data, for development and screenshots.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var demo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
            var composition = demo ? Composition.CreateDemo() : Composition.TryCreateWindows();
            if (composition is null)
            {
                MessageBox.Show(
                    "The configuration window is not connected to the Windows services in this build yet "
                    + $"({Composition.WindowsActionsTypeName} is missing).{Environment.NewLine}{Environment.NewLine}"
                    + "Start it with --demo to look at the window with sample data.",
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 2;
            }

            using var form = new MainForm(new ConfigFormModel(composition.Store), composition.Actions);
            if (demo) form.Text += " - demo data, nothing is saved to this computer";
            Application.Run(form);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The configuration window could not start:{Environment.NewLine}{Environment.NewLine}{ex.Message}", Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
