using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    private UpdateOffer? _offer;
    private SettingsWindow? _settingsWindow;

    Settings ISettingsHost.Settings => _settings;

    MicProcessor ISettingsHost.Mic => _mic;

    GlobalInput? ISettingsHost.Input => _input;

    bool ISettingsHost.InCall => _client is not null;

    public MainWindow()
    {
        InitializeComponent();
        _roomChat = new ChatPanel("Message the room");
        _roomChat.Send = text => SendSafeAsync(c => c.SendChatAsync(text));
        RoomChatHost.Content = _roomChat;
        DarkTitleBar.Apply(this);
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

        Title = $"The Comm-Spot {SelfUpdate.Current.ToString(3)}";
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
        ShareButton.Click += OnShare;
        DeafenButton.Click += OnDeafen;
        LeaveButton.Click += async (_, _) => await LeaveAsync(null);
        _refresh.Tick += (_, _) => RenderMembers();
        Closing += async (_, _) =>
        {
            _settingsWindow?.Close();
            foreach (var w in _dms.Values.ToList())
            {
                w.Close();
            }
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
            : "Voice activate: your mic sends when you speak.";
    }

    void ISettingsHost.SettingsChanged() => ApplySettings();

    void ISettingsHost.DevicesChanged()
    {
        if (_client is { } c)
        {
            StartAudio(c);
        }
    }

    Version? ISettingsHost.UpdateReady => _offer?.Version;

    void ISettingsHost.InstallUpdate() => OnUpdate(null, new RoutedEventArgs());

    async Task<string> ISettingsHost.CheckForUpdatesNowAsync()
    {
        if (!SelfUpdate.Supported)
        {
            return "Updates only work in the downloaded app.";
        }
        await CheckForUpdateAsync();
        return _offer is null ? "You're up to date." : $"Version {_offer.Version.ToString(3)} is ready.";
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
        if (_offer is not null && UpdateButton.IsEnabled)
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
        client.VideoFrameReceived += (_, frame) => _viewer?.ShowFrame(frame);
        client.ChatReceived += m => Dispatcher.UIThread.Post(() =>
        {
            var entry = new ChatEntry(m.FromName, m.Text, m.SentAt, m.FromUserId == client.UserId);
            _chats.Append(ChatStore.Room, entry);
            _roomChat.Add(entry);
        });
        client.DirectReceived += m => Dispatcher.UIThread.Post(() =>
        {
            bool mine = m.FromUserId == client.UserId;
            string peer = mine ? m.ToName : m.FromName;
            var entry = new ChatEntry(m.FromName, m.Text, m.SentAt, mine);
            _chats.Append(ChatStore.Direct(peer), entry);
            bool wasOpen = _dms.ContainsKey(peer.ToLowerInvariant());
            var w = OpenDirect(peer, activate: false);
            if (wasOpen)
            {
                w.Chat.Add(entry); // a new window already loaded it from history
            }
        });
        client.WatchEnded += _ => Dispatcher.UIThread.Post(() =>
        {
            _viewer?.Close();
            StatusText.Text = "They stopped sharing.";
        });
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
            string? err = await client.ConnectAsync(_settings.Server, KeyBox.Text ?? "", NameBox.Text ?? "");
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
        _settings.Name = NameBox.Text ?? "";
        _settings.Save();
        ApplySettings();
        StartAudio(client);

        JoinPanel.IsVisible = false;
        RoomPanel.IsVisible = true;
        _roomChat.Show(_chats.Load(ChatStore.Room));
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
        _sharer?.Dispose();
        _sharer = null;
        HidePreview();
        ShareButton.Content = "Share Screen";
        _viewer?.Close();
        _rendered = string.Empty;
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

    private readonly Dictionary<int, Border> _dots = [];
    private string _rendered = string.Empty;

    /// <summary>Rebuilds the member list only when it changes (so buttons stay clickable); updates talking dots every tick.</summary>
    private void RenderMembers()
    {
        if (_client is null)
        {
            return;
        }
        var members = _client.Members;
        string sig = string.Join('|', members.Select(m => $"{m.UserId}:{m.Name}:{m.Muted}:{m.Sharing}")) + $"#{_client.Watching}";
        if (sig != _rendered)
        {
            _rendered = sig;
            MemberList.Children.Clear();
            _dots.Clear();
            foreach (var m in members)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                var dot = new Border
                {
                    Width = 12, Height = 12, CornerRadius = new Avalonia.CornerRadius(6),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                _dots[m.UserId] = dot;
                row.Children.Add(dot);
                row.Children.Add(new TextBlock
                {
                    Text = m.Name + (m.UserId == _client.UserId ? " (you)" : ""),
                    Foreground = new SolidColorBrush(Color.Parse("#EDEDED")),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                if (m.UserId != _client.UserId)
                {
                    string dmName = m.Name;
                    var msg = new Button { Content = "Message", Padding = new Avalonia.Thickness(10, 2), VerticalAlignment = VerticalAlignment.Center };
                    msg.Click += (_, _) => OpenDirect(dmName, activate: true);
                    row.Children.Add(msg);
                }
                if (m.Muted)
                {
                    row.Children.Add(new TextBlock { Text = "muted", Foreground = new SolidColorBrush(Color.Parse("#E07A5F")), VerticalAlignment = VerticalAlignment.Center });
                }
                if (m.Sharing)
                {
                    row.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.Parse("#D64545")), CornerRadius = new Avalonia.CornerRadius(4),
                        Padding = new Avalonia.Thickness(6, 1), VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = "LIVE", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brushes.White },
                    });
                    if (m.UserId != _client.UserId)
                    {
                        int id = m.UserId;
                        string name = m.Name;
                        var watch = new Button { Content = _client.Watching == id ? "Watching" : "Watch", Padding = new Avalonia.Thickness(10, 2), VerticalAlignment = VerticalAlignment.Center };
                        watch.Click += async (_, _) => await WatchAsync(id, name);
                        row.Children.Add(watch);
                    }
                }
                MemberList.Children.Add(row);
            }
        }
        foreach (var m in members)
        {
            if (_dots.TryGetValue(m.UserId, out var dot))
            {
                bool talking = !m.Muted && _client.IsSpeaking(m.UserId);
                dot.Background = new SolidColorBrush(talking ? Color.Parse("#7BD389") : Color.Parse("#3A3A3A"));
            }
        }
    }

    // ---- Screen sharing ----
    // ---- Text chat ----
    private readonly ChatStore _chats = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CommSpot", "chats"));
    private readonly ChatPanel _roomChat;
    private readonly Dictionary<string, DirectMessageWindow> _dms = [];

    private async Task<string?> SendSafeAsync(Func<VoiceClient, Task<string?>> send)
    {
        if (_client is not { } c)
        {
            return "Join the room first.";
        }
        try
        {
            return await send(c);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or Microsoft.AspNetCore.SignalR.HubException or TaskCanceledException)
        {
            return "Couldn't send. Check your connection.";
        }
    }

    /// <summary>Opens (or brings up) the private chat with someone. Incoming messages open it without stealing focus.</summary>
    private DirectMessageWindow OpenDirect(string name, bool activate)
    {
        string key = name.ToLowerInvariant();
        if (_dms.TryGetValue(key, out var existing))
        {
            if (activate)
            {
                existing.Activate();
            }
            return existing;
        }
        var w = new DirectMessageWindow(name) { ShowActivated = activate };
        w.Chat.Show(_chats.Load(ChatStore.Direct(name)));
        w.Chat.Send = text => SendSafeAsync(c =>
        {
            var to = c.Members.FirstOrDefault(m => m.UserId != c.UserId && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            return to is null ? Task.FromResult<string?>($"{name} isn't in the room right now.") : c.SendDirectAsync(to.UserId, text);
        });
        w.Closed += (_, _) => _dms.Remove(key);
        _dms[key] = w;
        w.Show();
        return w;
    }

    private ScreenSharer? _sharer;
    private ScreenViewerWindow? _viewer;

    private async void OnShare(object? sender, RoutedEventArgs e)
    {
        if (_client is null)
        {
            return;
        }
        if (_sharer is not null)
        {
            await StopSharingAsync();
            return;
        }
        if (!ScreenCapture.Supported)
        {
            StatusText.Text = "Sharing your screen works from Windows for now. You can still watch others.";
            return;
        }
        var sources = ScreenCapture.ListSources(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
        var choice = await new ShareScreenWindow(sources, _settings).ShowDialog<ShareChoice?>(this);
        if (choice is null || _client is null)
        {
            return;
        }
        await _client.SetSharingAsync(true);
        _sharer = new ScreenSharer(_client, choice.Source, choice.MaxHeight, choice.Fps);
        _sharer.SourceLost += () => Dispatcher.UIThread.Post(async () =>
        {
            await StopSharingAsync();
            StatusText.Text = "The shared window was closed, so sharing stopped.";
        });
        _sharer.PreviewFrame += ShowPreview;
        ShareButton.Content = $"Stop Sharing ({choice.Source.Name})";
        PreviewLabel.Text = $"You're sharing {choice.Source.Name}";
        PreviewPanel.IsVisible = true;
    }

    private int _previewDecoding;

    /// <summary>Shows the sharer what they're streaming. Decodes off the UI thread and skips frames while busy.</summary>
    private void ShowPreview(byte[] jpeg)
    {
        if (Interlocked.Exchange(ref _previewDecoding, 1) == 1)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            Bitmap? bmp = null;
            try
            {
                using var ms = new MemoryStream(jpeg, writable: false);
                bmp = Bitmap.DecodeToWidth(ms, 480);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
            {
                bmp = null;
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (bmp is not null && _sharer is not null)
                {
                    var old = PreviewImage.Source as Bitmap;
                    PreviewImage.Source = bmp;
                    old?.Dispose();
                }
                else
                {
                    bmp?.Dispose();
                }
                Interlocked.Exchange(ref _previewDecoding, 0);
            });
        });
    }

    private void HidePreview()
    {
        PreviewPanel.IsVisible = false;
        var old = PreviewImage.Source as Bitmap;
        PreviewImage.Source = null;
        old?.Dispose();
    }

    private async Task StopSharingAsync()
    {
        _sharer?.Dispose();
        _sharer = null;
        HidePreview();
        ShareButton.Content = "Share Screen";
        if (_client is { Sharing: true } c)
        {
            await c.SetSharingAsync(false);
        }
    }

    private async Task WatchAsync(int userId, string name)
    {
        if (_client is null)
        {
            return;
        }
        if (_client.Watching == userId && _viewer is not null)
        {
            _viewer.Activate();
            return;
        }
        _viewer?.Close();
        var viewer = new ScreenViewerWindow(name);
        _viewer = viewer;
        viewer.Closed += async (_, _) =>
        {
            if (ReferenceEquals(_viewer, viewer))
            {
                _viewer = null;
                if (_client is { } c && c.Watching == userId)
                {
                    await c.WatchAsync(0);
                }
            }
        };
        await _client.WatchAsync(userId);
        viewer.Show();
    }
}
