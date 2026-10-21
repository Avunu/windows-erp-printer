using ErpPrinter.Outbox;
using ErpPrinter.Service;
using ErpPrinter.Windows.Hosting;
using ErpPrinter.Windows.Listening;

// One process hosts the listener (pipes), the uploader (outbox -> ERP) and the updater. Running it from a console
// works too (for debugging): it then logs to the console as well and stops on Ctrl+C.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = StatusServiceName.Value);
// Our own log files and the Application event log (source ErpPrinter) are the product's logging; the host's default
// providers would add a second, differently formatted stream under the same event source.
builder.Logging.ClearProviders();

builder.Services.AddSingleton(_ => new ErpPrinterContext("service"));
builder.Services.AddSingleton<IOutboxSignal>(_ => new NamedOutboxSignal());
builder.Services.AddHostedService<ListenerWorker>();
builder.Services.AddHostedService<UploaderWorker>();
builder.Services.AddHostedService<UpdaterWorker>();

await builder.Build().RunAsync();

namespace ErpPrinter.Service
{
    internal static class StatusServiceName
    {
        public const string Value = "ErpPrinter";
    }
}
