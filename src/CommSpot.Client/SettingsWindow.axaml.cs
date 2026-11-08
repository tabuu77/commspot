using Avalonia.Controls;
using Avalonia.Threading;
using CommSpot.Client.Core;
using PortAudioSharp;

namespace CommSpot.Client;

/// <summary>What the settings window needs from the main window.</summary>
internal interface ISettingsHost
{
    Settings Settings { get; }

    MicProcessor Mic { get; }

    GlobalInput? Input { get; }

    bool InCall { get; }

    /// <summary>Voice mode, key, delay or sensitivity changed.</summary>
    void SettingsChanged();

    /// <summary>Mic or speaker device changed.</summary>
    void DevicesChanged();

    Task<string> CheckForUpdatesNowAsync();
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The mic test engine is disposed in StopMicTest, which runs on Closed.")]
public partial class SettingsWindow : Window
{
    private readonly ISettingsHost _host;
    private readonly Settings _s;
    private List<(int Index, string Name)> _mics = [];
    private List<(int Index, string Name)> _speakers = [];
    private readonly DispatcherTimer _meter = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _capture = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private bool _captureArmed;
    private long _captureEnds;
    private bool _loading = true;
    private AudioEngine? _testEngine;

    public SettingsWindow()
    {
        // For the XAML designer only.
        InitializeComponent();
        _host = null!;
        _s = new Settings();
    }

    internal SettingsWindow(ISettingsHost host)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _host = host;
        _s = host.Settings;

        LoadDevices();
        MicVolumeSlider.Value = _s.MicVolume * 100;
        SpeakerVolumeSlider.Value = _s.SpeakerVolume * 100;
        SensitivitySlider.Value = _s.Sensitivity;
        AutoSensitivityBox.IsChecked = _s.AutoSensitivity;
        SensitivitySlider.IsEnabled = !_s.AutoSensitivity;
        VadRadio.IsChecked = !_s.PushToTalk;
        PttRadio.IsChecked = _s.PushToTalk;
        ReleaseSlider.Value = _s.PushToTalkReleaseMs;
        VersionText.Text = $"You're on The Comm-Spot {SelfUpdate.Current.ToString(3)}.";
        ServerBox.Text = _s.Server;
        UpdateLabels();
        ShowKey();
        _loading = false;

        MicBox.SelectionChanged += (_, _) => DeviceChanged(input: true);
        SpeakerBox.SelectionChanged += (_, _) => DeviceChanged(input: false);
        MicVolumeSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) Changed(() => _s.MicVolume = MicVolumeSlider.Value / 100); };
        SpeakerVolumeSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) Changed(() => _s.SpeakerVolume = SpeakerVolumeSlider.Value / 100); };
        SensitivitySlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) Changed(() => _s.Sensitivity = SensitivitySlider.Value); };
        AutoSensitivityBox.IsCheckedChanged += (_, _) => Changed(() =>
        {
            _s.AutoSensitivity = AutoSensitivityBox.IsChecked == true;
            SensitivitySlider.IsEnabled = !_s.AutoSensitivity;
        });
        PttRadio.IsCheckedChanged += (_, _) => Changed(() => _s.PushToTalk = PttRadio.IsChecked == true);
        ReleaseSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) Changed(() => _s.PushToTalkReleaseMs = (int)ReleaseSlider.Value); };
        ServerBox.LostFocus += (_, _) =>
        {
            string server = (ServerBox.Text ?? "").Trim();
            _s.Server = server.Length == 0 ? "commspot.net" : server;
            ServerBox.Text = _s.Server;
            _s.Save();
        };
        PttKeyButton.Click += (_, _) => StartCapture();
        _capture.Tick += (_, _) => CaptureTick();
        MicTestButton.Click += (_, _) => ToggleMicTest();
        _meter.Tick += (_, _) => MicMeter.Value = _host.Mic.Level;
        _meter.Start();
        CheckUpdatesButton.Click += async (_, _) =>
        {
            CheckUpdatesButton.IsEnabled = false;
            UpdateStatus.Text = "Checking...";
            UpdateStatus.Text = await _host.CheckForUpdatesNowAsync();
            CheckUpdatesButton.IsEnabled = true;
        };
        Closed += (_, _) =>
        {
            _meter.Stop();
            _capture.Stop();
            StopMicTest();
            string server = (ServerBox.Text ?? "").Trim();
            _s.Server = server.Length == 0 ? "commspot.net" : server;
            _s.Save();
            _host.SettingsChanged();
        };
    }

    private void Changed(Action apply)
    {
        if (_loading)
        {
            return;
        }
        apply();
        UpdateLabels();
        _s.Save();
        _host.SettingsChanged();
    }

    private void UpdateLabels()
    {
        MicVolumeLabel.Text = $"Microphone volume: {MicVolumeSlider.Value:0}%";
        SpeakerVolumeLabel.Text = $"Speaker volume: {SpeakerVolumeSlider.Value:0}%";
        ReleaseLabel.Text = $"Push to talk release delay: {ReleaseSlider.Value:0} ms";
    }

    private void LoadDevices()
    {
        try
        {
            _mics = AudioEngine.Devices(input: true).ToList();
            _speakers = AudioEngine.Devices(input: false).ToList();
            Fill(MicBox, _mics, _s.Mic, AudioEngine.DefaultDevice(true));
            Fill(SpeakerBox, _speakers, _s.Speaker, AudioEngine.DefaultDevice(false));
        }
        catch (Exception ex) when (ex is DllNotFoundException or PortAudioException or TypeInitializationException)
        {
            MicTestButton.IsEnabled = false;
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
        box.SelectedIndex = devices.Count == 0 ? -1 : Math.Max(0, i);
    }

    private void DeviceChanged(bool input)
    {
        if (_loading)
        {
            return;
        }
        if (input)
        {
            _s.Mic = MicBox.SelectedItem as string;
        }
        else
        {
            _s.Speaker = SpeakerBox.SelectedItem as string;
        }
        _s.Save();
        bool testing = _testEngine is not null;
        StopMicTest();
        _host.DevicesChanged();
        if (testing)
        {
            ToggleMicTest();
        }
    }

    private void ToggleMicTest()
    {
        if (_host.Mic.Loopback)
        {
            StopMicTest();
            return;
        }
        _host.Mic.Loopback = true;
        MicTestButton.Content = "Stop Testing";
        if (!_host.InCall)
        {
            // Outside a call, open the devices just for the test.
            try
            {
                _testEngine = new AudioEngine(new MicTest(_host.Mic));
                _testEngine.Start(DeviceIndex(_mics, MicBox), DeviceIndex(_speakers, SpeakerBox));
            }
            catch (Exception ex) when (ex is PortAudioException or DllNotFoundException)
            {
                StopMicTest();
                MicTestButton.Content = "Mic test failed";
            }
        }
    }

    private static int DeviceIndex(List<(int Index, string Name)> list, ComboBox box) =>
        box.SelectedIndex >= 0 && box.SelectedIndex < list.Count ? list[box.SelectedIndex].Index : PortAudio.NoDevice;

    /// <summary>Called when a call starts or ends, so two things never hold the sound card.</summary>
    internal void ResetMicTest() => StopMicTest();

    private void StopMicTest()
    {
        _host.Mic.Loopback = false;
        _testEngine?.Dispose();
        _testEngine = null;
        MicTestButton.Content = "Mic Test";
    }

    private void ShowKey()
    {
        if (_host.Input is null)
        {
            PttKeyButton.IsEnabled = false;
            PttKeyButton.Content = "Push to talk isn't available on this system";
            return;
        }
        PttKeyButton.Content = _s.PushToTalkKey is { } k ? $"{_host.Input.Describe(k)}  (click to change)" : "Set key";
    }

    private void StartCapture()
    {
        if (_host.Input is null)
        {
            return;
        }
        _captureArmed = false;
        _captureEnds = Environment.TickCount64 + 10_000;
        PttKeyButton.Content = "Press a key or mouse button...  (Esc cancels)";
        _capture.Start();
    }

    private void CaptureTick()
    {
        string? pressed = _host.Input!.FindPressed();
        if (!_captureArmed)
        {
            _captureArmed = pressed is null; // wait until everything is released first
        }
        else if (pressed is not null)
        {
            if (!_host.Input.IsEscape(pressed))
            {
                _s.PushToTalkKey = pressed;
                _s.Save();
                _host.SettingsChanged();
            }
            StopCapture();
            return;
        }
        if (Environment.TickCount64 > _captureEnds)
        {
            StopCapture();
        }
    }

    private void StopCapture()
    {
        _capture.Stop();
        _captureArmed = false;
        ShowKey();
    }
}
