using System.IO;
using System.Windows;
using System.Windows.Threading;
using HoshinoTransfer.Windows.Services;
using HoshinoTransfer.Windows.ViewModels;

namespace HoshinoTransfer.Windows;

public partial class App : Application
{
    private int _dispatcherFaults;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        if (e.Args.Any(arg => string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            try { Environment.ExitCode = await SelfTest.RunAsync(); }
            catch (Exception ex) { WriteClientLog(ex); Environment.ExitCode = 2; }
            Shutdown();
            return;
        }

        var viewModel = new MainViewModel(new ApiClient());
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();
        try { await viewModel.TryRestoreSessionAsync(); }
        catch (Exception ex) { WriteClientLog(ex); }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteClientLog(e.Exception);
        e.Handled = true;
        _dispatcherFaults++;
        if (_dispatcherFaults == 1)
            MessageBox.Show("An unexpected problem occurred. Details were saved to the local application log.", "HoshinoTransfer", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (_dispatcherFaults >= 3) Shutdown(2);
    }

    private static void WriteClientLog(Exception exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoshinoTransfer", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "client.log"), $"[{DateTimeOffset.Now:O}] {exception}\r\n");
        }
        catch { }
    }
}
