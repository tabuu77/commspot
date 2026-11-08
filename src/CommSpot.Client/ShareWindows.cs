using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace CommSpot.Client;

internal sealed record ShareChoice(ShareSource Source, int MaxHeight, int Fps);

/// <summary>Pick what to share and how.</summary>
internal sealed class ShareScreenWindow : Window
{
    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#1B1714"));
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#B8A99A"));

    public ShareScreenWindow(IReadOnlyList<ShareSource> sources, Settings settings)
    {
        Title = "Share your screen";
        Width = 520;
        Height = 520;
        Background = Bg;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var list = new ListBox { ItemsSource = sources, Height = 280, SelectedIndex = sources.Count > 0 ? 0 : -1 };
        var quality = new ComboBox { ItemsSource = new[] { "720p", "1080p" }, SelectedIndex = settings.ShareHeight >= 1080 ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var fps = new ComboBox { ItemsSource = new[] { "5 fps", "15 fps", "30 fps" }, SelectedIndex = settings.ShareFps switch { <= 5 => 0, >= 30 => 2, _ => 1 }, HorizontalAlignment = HorizontalAlignment.Stretch };
        var go = new Button { Content = "Go Live", Background = new SolidColorBrush(Color.Parse("#F2B544")), Foreground = Bg, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(0, 10) };
        var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(0, 10) };
        go.Click += (_, _) =>
        {
            if (list.SelectedItem is ShareSource s)
            {
                settings.ShareHeight = quality.SelectedIndex == 1 ? 1080 : 720;
                settings.ShareFps = fps.SelectedIndex switch { 0 => 5, 2 => 30, _ => 15 };
                settings.Save();
                Close(new ShareChoice(s, settings.ShareHeight, settings.ShareFps));
            }
        };
        cancel.Click += (_, _) => Close(null);

        var options = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        options.Children.Add(Stack("Quality", quality));
        var f = Stack("Frame rate", fps);
        Grid.SetColumn(f, 2);
        options.Children.Add(f);
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel);
        Grid.SetColumn(go, 2);
        buttons.Children.Add(go);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = "Choose a screen or window", Foreground = Label, Margin = new Thickness(0, 0, 0, 6) },
                list,
                options,
                buttons,
            },
        };
    }

    private static StackPanel Stack(string label, Control c) => new()
    {
        Margin = new Thickness(0, 12, 0, 0),
        Children = { new TextBlock { Text = label, Foreground = Label, Margin = new Thickness(0, 0, 0, 4) }, c },
    };
}

/// <summary>Shows someone's shared screen. Double-click for full screen, Esc to leave it.</summary>
internal sealed class ScreenViewerWindow : Window
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _waiting = new() { Text = "Waiting for the first frame...", Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private int _decoding;

    public ScreenViewerWindow(string sharerName)
    {
        Title = $"{sharerName}'s screen - CommSpot";
        Width = 1100;
        Height = 660;
        Background = Brushes.Black;
        Content = new Grid { Children = { _waiting, _image } };
        DoubleTapped += (_, _) => ToggleFullScreen();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && WindowState == WindowState.FullScreen)
            {
                WindowState = WindowState.Normal;
            }
            else if (e.Key == Key.F11)
            {
                ToggleFullScreen();
            }
        };
    }

    private void ToggleFullScreen() =>
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

    /// <summary>Called from the network thread. Decodes off the UI thread; if still busy, the frame is skipped.</summary>
    public void ShowFrame(byte[] jpeg)
    {
        if (Interlocked.Exchange(ref _decoding, 1) == 1)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            Bitmap? bmp = null;
            try
            {
                using var ms = new MemoryStream(jpeg, writable: false);
                bmp = new Bitmap(ms);
                if (bmp.PixelSize.Width > Protocol.VoiceConstants.MaxVideoDimension
                    || bmp.PixelSize.Height > Protocol.VoiceConstants.MaxVideoDimension)
                {
                    bmp.Dispose();
                    bmp = null;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
            {
                bmp = null; // malformed frame from the network: skip it
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (bmp is not null)
                {
                    var old = _image.Source as Bitmap;
                    _image.Source = bmp;
                    _waiting.IsVisible = false;
                    old?.Dispose();
                }
                Interlocked.Exchange(ref _decoding, 0);
            });
        });
    }
}
