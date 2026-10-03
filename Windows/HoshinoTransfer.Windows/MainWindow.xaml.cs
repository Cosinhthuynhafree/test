using System.Windows;
using HoshinoTransfer.Windows.ViewModels;

namespace HoshinoTransfer.Windows;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AllowDrop = true;
        Drop += OnWindowDrop;
        if (DataContext is MainViewModel viewModel) viewModel.PasswordCleared += ClearPasswordBox;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel fresh) fresh.PasswordCleared += ClearPasswordBox;
        };
    }

    private void ClearPasswordBox() => Dispatcher.Invoke(() => PasswordInput.Clear());

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        if (DataContext is MainViewModel viewModel) viewModel.SendDroppedFiles(paths);
    }

    private void PasswordInput_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) viewModel.Password = PasswordInput.Password;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is IDisposable disposable) disposable.Dispose();
        base.OnClosed(e);
    }
}
