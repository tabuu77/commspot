using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommSpot.Client.Core;
using PortAudioSharp;

namespace CommSpot.Client;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Audio and client are disposed in LeaveAsync, which runs on window Closing.")]
public partial class MainWindow : Window, ISettingsHost
{
    private readonly Settings _settings = Settings.Load();
    private readonly MicProcessor _mic = new();
    private VoiceClient? _client;
    private AudioEngine? _audio;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly GlobalInput? _input = GlobalInput.Create();
    private readonly PushToTalkWatcher? _ptt;
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private UpdateOffer? _offer;
    private SettingsWindow? _settingsWindow;

    Settings ISettingsHost.Settings => _settings;

    MicProcessor ISettingsHost.Mic => _mic;

    GlobalInput? ISettingsHost.Input => _input;

    bool ISettingsHost.InCall => _client is not null;

    public MainWindow()
    {
        InitializeComponent();
        ServerBox.Text = _settings.Server;
        NameBox.Text = _settings.Name;
        if (_input is not null)
        {
            _ptt = new PushToTalkWatcher(_input, held =>
            {
                if (_client is { } c)
                {
                    c.PushToTalkHeld = held;
                }
            });
        }
        ApplySettings();

        Title = $"CommSpot {SelfUpdate.Current.ToString(3)}";
        SelfUpdate.CleanupOld();
        UpdateButton.Click += OnUpdate;
        _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
        Opened += async (_, _) =>
        {
            _updateTimer.Start();
            await CheckForUpdateAsync();
        };

        SettingsButton.Click += (_, _) => OpenSettings();
        JoinButton.Click += OnJoin;
        MuteButton.Click += OnMute;
        DeafenButton.Click += OnDeafen;
        LeaveButton.Click += async (_, _) => await LeaveAsync(null);
        _refresh.Tick += (_, _) => RenderMembers();
        Closing += async (_, _) =>
        {
            _settingsWindow?.Close();
            _settings.Save();
            await LeaveAsync(null);
        };
        Closed += (_, _) =>
        {
            _ptt?.Dispose();
            _input?.Dispose();
        };
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show(this);
    }

    /// <summary>Pushes saved settings into the mic processor, the push-to-talk watcher and the live call.</summary>
    private void ApplySettings()
    {
        _mic.Gain = _settings.MicVolume;
        _mic.SpeakerGain = _settings.SpeakerVolume;
        if (_ptt is not null)
        {
            _ptt.Binding = _settings.PushToTalk ? _settings.PushToTalkKey : null;
        }
        if (_client is { } c)
        {
            c.PushToTalk = _settings.PushToTalk;
            c.PushToTalkReleaseMs = Math.Clamp(_settings.PushToTalkReleaseMs, 0, 1000);
            c.Activity.Auto = _settings.AutoSensitivity;
            if (!_settings.AutoSensitivity)
            {
                c.Activity.Threshold = _settings.Sensitivity;
            }
        }
        ModeHint.Text = _settings.PushToTalk
            ? _settings.PushToTalkKey is { } k && _input is not null
                ? $"Push to talk: hold {_input.Describe(k)} to talk."
                : "Push to talk is on, but no key is set. Open Settings to pick one."
            : "Voice activity: your mic sends when you speak.";
    }

    void ISettingsHost.SettingsChanged() => ApplySettings();

    void ISettingsHost.DevicesChanged()
    {
        if (_client is { } c)
        {
            StartAudio(c);
        }
    }

    async Task<string> ISettingsHost.CheckForUpdatesNowAsync()
    {
        if (!SelfUpdate.Supported)
        {
            return "Updates only work in the downloaded app.";
        }
        await CheckForUpdateAsync();
        return _offer is null ? "You're up to date." : $"Version {_offer.Version.ToString(3)} is ready. Use the gold button in the main window.";
    }

    private static int ResolveDevice(bool input, string? saved)
    {
        var list = AudioEngine.Devices(input);
        foreach (var d in list)
        {
            if (d.Name == saved)
            {
                return d.Index;
            }
        }
        int def = AudioEngine.DefaultDevice(input);
        return def != PortAudio.NoDevice ? def : list.Count > 0 ? list[0].Index : PortAudio.NoDevice;
    }

    private void StartAudio(VoiceClient client)
    {
        _audio?.Dispose();
        _audio = null;
        try
        {
            _audio = new AudioEngine(client);
            _audio.Start(ResolveDevice(true, _settings.Mic), ResolveDevice(false, _settings.Speaker));
            StatusText.Text = string.Empty;
        }
        catch (Exception ex) when (ex is PortAudioException or DllNotFoundException or TypeInitializationException)
        {
            StatusText.Text = "Joined, but the audio device failed: " + ex.Message;
        }
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            _offer = await SelfUpdate.CheckAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException
                                       or TaskCanceledException or System.Text.Json.JsonException or FormatException)
        {
            return; // offline or bad manifest: keep the current version, try again later
        }
        if (_offer is not null)
        {
            UpdateButton.Content = $"Update to {_offer.Version.ToString(3)} available. Click to update and restart";
            UpdateButton.IsVisible = true;
        }
    }

    private async void OnUpdate(object? sender, RoutedEventArgs e)
    {
        if (_offer is null)
        {
            return;
        }
        UpdateButton.IsEnabled = false;
        var progress = new Progress<double>(p => UpdateButton.Content = $"Downloading update... {p:P0}");
        try
        {
            await LeaveAsync(null);
            await SelfUpdate.ApplyAsync(_offer, progress);
            _settings.Save();
            Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException
                                       or UnauthorizedAccessException or TaskCanceledException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            UpdateButton.Content = "Update failed, kept this version. Click to retry";
            StatusText.Text = ex.Message;
            UpdateButton.IsEnabled = true;
        }
    }

    private async void OnJoin(object? sender, RoutedEventArgs e)
    {
        JoinButton.IsEnabled = false;
        StatusText.Text = "Connecting...";
        _settingsWindow?.ResetMicTest();
        var client = new VoiceClient { Mic = _mic };
        client.Disconnected += msg => Dispatcher.UIThread.Post(async () =>
        {
            // Leaving on purpose also closes the connection; only report drops of the live session.
            if (ReferenceEquals(_client, client))
            {
                await LeaveAsync("Connection lost: " + msg);
            }
        });
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
        _settings.Save();
        ApplySettings();
        StartAudio(client);

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
        _settingsWindow?.ResetMicTest();
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
