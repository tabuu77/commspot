using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommSpot.Client.Core;
using PortAudioSharp;

namespace CommSpot.Client;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Audio and client are disposed in LeaveAsync, which runs on window Closing.")]
public partial class MainWindow : Window
{
    private readonly Settings _settings = Settings.Load();
    private List<(int Index, string Name)> _mics = [];
    private List<(int Index, string Name)> _speakers = [];
    private VoiceClient? _client;
    private AudioEngine? _audio;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(120) };

    public MainWindow()
    {
        InitializeComponent();
        ServerBox.Text = _settings.Server;
        NameBox.Text = _settings.Name;
        LoadDevices();

        JoinButton.Click += OnJoin;
        MuteButton.Click += OnMute;
        DeafenButton.Click += OnDeafen;
        LeaveButton.Click += async (_, _) => await LeaveAsync(null);
        SensitivitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty && _client is not null)
            {
                _client.Activity.Threshold = SensitivitySlider.Value;
            }
        };
        _refresh.Tick += (_, _) => RenderMembers();
        Closing += async (_, _) => await LeaveAsync(null);
    }

    private void LoadDevices()
    {
        try
        {
            _mics = AudioEngine.Devices(input: true).ToList();
            _speakers = AudioEngine.Devices(input: false).ToList();
            Fill(MicBox, _mics, _settings.Mic, AudioEngine.DefaultDevice(true));
            Fill(SpeakerBox, _speakers, _settings.Speaker, AudioEngine.DefaultDevice(false));
        }
        catch (Exception ex) when (ex is DllNotFoundException or PortAudioException or TypeInitializationException)
        {
            StatusText.Text = "Couldn't open the sound system: " + ex.Message;
        }
    }

    private static void Fill(ComboBox box, List<(int Index, string Name)> devices, string? saved, int fallback)
    {
        box.ItemsSource = devices.Select(d => d.Name).ToList();
        int i = devices.FindIndex(d => d.Name == saved);
        if (i < 0)
        {
            i = devices.FindIndex(d => d.Index == fallback);
        }
        box.SelectedIndex = Math.Max(0, i);
    }

    private async void OnJoin(object? sender, RoutedEventArgs e)
    {
        JoinButton.IsEnabled = false;
        StatusText.Text = "Connecting...";
        var client = new VoiceClient();
        client.Activity.Threshold = SensitivitySlider.Value;
        client.Disconnected += msg => Dispatcher.UIThread.Post(async () => await LeaveAsync("Disconnected: " + msg));
        try
        {
            string? err = await client.ConnectAsync(ServerBox.Text ?? "", KeyBox.Text ?? "", NameBox.Text ?? "");
            if (err is not null)
            {
                await client.DisposeAsync();
                StatusText.Text = err;
                JoinButton.IsEnabled = true;
                return;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException
                                       or InvalidOperationException or IOException or TaskCanceledException)
        {
            await client.DisposeAsync();
            StatusText.Text = "Couldn't reach the server: " + ex.Message;
            JoinButton.IsEnabled = true;
            return;
        }

        _client = client;
        KeyBox.Text = string.Empty; // don't keep the key in the UI
        _settings.Server = ServerBox.Text ?? "commspot.net";
        _settings.Name = NameBox.Text ?? "";
        _settings.Mic = MicBox.SelectedItem as string;
        _settings.Speaker = SpeakerBox.SelectedItem as string;
        _settings.Save();

        try
        {
            _audio = new AudioEngine(client);
            _audio.Start(
                MicBox.SelectedIndex >= 0 && _mics.Count > 0 ? _mics[MicBox.SelectedIndex].Index : PortAudio.NoDevice,
                SpeakerBox.SelectedIndex >= 0 && _speakers.Count > 0 ? _speakers[SpeakerBox.SelectedIndex].Index : PortAudio.NoDevice);
            StatusText.Text = string.Empty;
        }
        catch (Exception ex) when (ex is PortAudioException or DllNotFoundException)
        {
            StatusText.Text = "Joined, but the audio device failed: " + ex.Message;
        }

        JoinPanel.IsVisible = false;
        RoomPanel.IsVisible = true;
        _refresh.Start();
        RenderMembers();
    }

    private async void OnMute(object? sender, RoutedEventArgs e)
    {
        if (_client is null)
        {
            return;
        }
        await _client.SetMutedAsync(!_client.Muted);
        MuteButton.Content = _client.Muted ? "Unmute" : "Mute";
    }

    private void OnDeafen(object? sender, RoutedEventArgs e)
    {
        if (_client is null)
        {
            return;
        }
        _client.Deafened = !_client.Deafened;
        DeafenButton.Content = _client.Deafened ? "Undeafen" : "Deafen";
    }

    private async Task LeaveAsync(string? message)
    {
        _refresh.Stop();
        _audio?.Dispose();
        _audio = null;
        if (_client is not null)
        {
            var c = _client;
            _client = null;
            await c.DisposeAsync();
        }
        RoomPanel.IsVisible = false;
        JoinPanel.IsVisible = true;
        JoinButton.IsEnabled = true;
        MuteButton.Content = "Mute";
        DeafenButton.Content = "Deafen";
        if (message is not null)
        {
            StatusText.Text = message;
        }
    }

    private void RenderMembers()
    {
        if (_client is null)
        {
            return;
        }
        MemberList.Children.Clear();
        foreach (var m in _client.Members)
        {
            bool talking = !m.Muted && _client.IsSpeaking(m.UserId);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new Border
            {
                Width = 12, Height = 12, CornerRadius = new Avalonia.CornerRadius(6),
                Background = new SolidColorBrush(talking ? Color.Parse("#7BD389") : Color.Parse("#4A403A")),
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = m.Name + (m.UserId == _client.UserId ? " (you)" : ""),
                Foreground = new SolidColorBrush(Color.Parse("#EDE3D9")),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (m.Muted)
            {
                row.Children.Add(new TextBlock { Text = "muted", Foreground = new SolidColorBrush(Color.Parse("#E07A5F")), VerticalAlignment = VerticalAlignment.Center });
            }
            MemberList.Children.Add(row);
        }
    }
}
