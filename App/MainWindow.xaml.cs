using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mosslight.Update.App;

public partial class MainWindow : Window
{
    private bool _allowClose;
    public LauncherViewModel ViewModel { get; }
    public MainWindow(LauncherViewModel model)
    {
        InitializeComponent(); ViewModel = model; DataContext = model;
        model.CloseRequested += () => { _allowClose = true; Application.Current.Shutdown(); };
        Closing += Window_Closing;
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !ViewModel.Busy) { Application.Current.Shutdown(); return; }
        e.Cancel = true;
        if (!ViewModel.Critical) ViewModel.CancelAndClose();
    }
    private void Home_Click(object sender, RoutedEventArgs e) { HomeView.Visibility = Visibility.Visible; SettingsView.Visibility = Visibility.Collapsed; }
    private void Settings_Click(object sender, RoutedEventArgs e) { HomeView.Visibility = Visibility.Collapsed; SettingsView.Visibility = Visibility.Visible; }
    public void SavePreview(string path, string state = "home")
    {
        if (state == "settings") { HomeView.Visibility = Visibility.Collapsed; SettingsView.Visibility = Visibility.Visible; }
        CaptureRoot.Width = 1144; CaptureRoot.Height = 750;
        CaptureRoot.Measure(new Size(1144, 750)); CaptureRoot.Arrange(new Rect(0, 0, 1144, 750)); CaptureRoot.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1144, 750, 96, 96, PixelFormats.Pbgra32); bitmap.Render(CaptureRoot);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
