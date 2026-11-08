using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CommSpot.Client.Core;

namespace CommSpot.Client;

/// <summary>A message list with a text box under it. Used for the room chat and for private message windows.</summary>
internal sealed class ChatPanel : Border
{
    private const int MaxShown = 300;
    private static readonly IBrush NameBrush = new SolidColorBrush(Color.Parse("#A6A6A6"));
    private static readonly IBrush MineBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#EDEDED"));
    private static readonly IBrush TimeBrush = new SolidColorBrush(Color.Parse("#5A5A5A"));
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input;
    private readonly TextBlock _error = new() { Foreground = new SolidColorBrush(Color.Parse("#E07A5F")), FontSize = 12, IsVisible = false, TextWrapping = TextWrapping.Wrap };

    /// <summary>Called with the text to send; returns an error to show, or null.</summary>
    public Func<string, Task<string?>>? Send { get; set; }

    public ChatPanel(string placeholder)
    {
        Background = new SolidColorBrush(Color.Parse("#141414"));
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(10);
        _scroll = new ScrollViewer { Content = _list };
        _input = new TextBox { PlaceholderText = placeholder, MaxLength = Protocol.ChatText.MaxLength, AcceptsReturn = false, TextWrapping = TextWrapping.Wrap };
        var send = new Button { Content = "Send", VerticalAlignment = VerticalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center };
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(_input);
        Grid.SetColumn(send, 2);
        bottom.Children.Add(send);
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto") };
        root.Children.Add(_scroll);
        Grid.SetRow(_error, 1);
        root.Children.Add(_error);
        Grid.SetRow(bottom, 2);
        root.Children.Add(bottom);
        Child = root;

        send.Click += async (_, _) => await SubmitAsync();
        // Tunnel, so Enter sends before the text box sees it.
        _input.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return && (e.KeyModifiers & KeyModifiers.Shift) == 0)
            {
                e.Handled = true;
                await SubmitAsync();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public void FocusInput() => _input.Focus();

    private async Task SubmitAsync()
    {
        string text = _input.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) || Send is null)
        {
            return;
        }
        _input.Text = string.Empty;
        string? err = await Send(text);
        _error.Text = err ?? string.Empty;
        _error.IsVisible = err is not null;
        if (err is not null)
        {
            _input.Text = text; // keep what they typed
        }
    }

    public void Show(IEnumerable<ChatEntry> entries)
    {
        _list.Children.Clear();
        foreach (var e in entries.TakeLast(MaxShown))
        {
            _list.Children.Add(Row(e));
        }
        _scroll.ScrollToEnd();
    }

    public void Add(ChatEntry e)
    {
        bool atBottom = _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 4;
        _list.Children.Add(Row(e));
        if (_list.Children.Count > MaxShown)
        {
            _list.Children.RemoveAt(0);
        }
        if (atBottom || e.Mine)
        {
            _scroll.ScrollToEnd();
        }
    }

    private static StackPanel Row(ChatEntry e)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock { Text = e.From, FontWeight = FontWeight.SemiBold, Foreground = e.Mine ? MineBrush : NameBrush });
        head.Children.Add(new TextBlock
        {
            Text = DateTimeOffset.FromUnixTimeMilliseconds(e.At).ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture),
            FontSize = 11, Foreground = TimeBrush, VerticalAlignment = VerticalAlignment.Bottom,
        });
        var row = new StackPanel { Spacing = 1 };
        row.Children.Add(head);
        // Plain text only: nothing in a message is ever treated as a link, markup or command.
        row.Children.Add(new SelectableTextBlock { Text = e.Text, Foreground = TextBrush, TextWrapping = TextWrapping.Wrap });
        return row;
    }
}

/// <summary>Private messages with one person, in their own window. History is saved on this PC.</summary>
internal sealed class DirectMessageWindow : Window
{
    public ChatPanel Chat { get; }

    public string PeerName { get; }

    public DirectMessageWindow(string peerName)
    {
        DarkTitleBar.Apply(this);
        PeerName = peerName;
        Title = $"{peerName} - The Comm-Spot";
        Width = 420;
        Height = 520;
        MinWidth = 320;
        MinHeight = 300;
        Background = Brushes.Black;
        Chat = new ChatPanel($"Message {peerName}");
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = peerName, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 10) });
        Grid.SetRow(Chat, 1);
        root.Children.Add(Chat);
        Content = root;
        Opened += (_, _) => Chat.FocusInput();
    }
}
