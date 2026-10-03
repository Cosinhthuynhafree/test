using System.Windows;
using HoshinoTransfer.Windows.ViewModels;

namespace HoshinoTransfer.Windows;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

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
