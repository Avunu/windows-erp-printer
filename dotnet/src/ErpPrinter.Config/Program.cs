namespace ErpPrinter.Config;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        // The configuration form is ported in a later step.
        MessageBox.Show("ERP Printer configuration (C# port) is not implemented yet.", "ERP Printer");
    }
}
