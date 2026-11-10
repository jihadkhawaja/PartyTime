using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PartyTime.Audio;
using PartyTime.Interop;
using PartyTime.Settings;
using PartyTime.Visualization;
using Forms = System.Windows.Forms;

namespace PartyTime.Views;

public partial class ControlWindow : Window
{
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ToolStripMenuItem _trayToggle;
    private readonly SystemAudio _audio;
    private readonly DispatcherTimer _saveTimer;
    private VisualizerSettings _settings = VisualizerSettings.Load();
    private Overlay? _overlay;
    private HwndSource? _source;
    private bool _hotkeyRegistered;
    private bool _exiting;
    private bool _ready;

    public ControlWindow()
    {
        InitializeComponent();
        _audio = new SystemAudio(Dispatcher);
        _audio.StatusChanged += SetAudioStatus;
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _saveTimer.Tick += (_, _) => SaveSettings();
        Preview.GetScreenColors = () => _overlay?.ScreenColors;
        RestoreControls();
        RefreshDescriptions();
        ApplySettings();
        _ready = true;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open controls", null, (_, _) => OpenControls());
        _trayToggle = new Forms.ToolStripMenuItem("Start party", null, (_, _) => ToggleParty());
        menu.Items.Add(_trayToggle);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit PartyTime", null, (_, _) => ExitPartyTime());

        using var iconStream = Application.GetResourceStream(new Uri("/Assets/PartyTime-v2.ico", UriKind.Relative)).Stream;
        using var icon = new System.Drawing.Icon(iconStream);
        _tray = new Forms.NotifyIcon
        {
            Icon = (System.Drawing.Icon)icon.Clone(),
            ContextMenuStrip = menu,
            Text = "PartyTime · Off",
            Visible = true
        };
        _tray.DoubleClick += (_, _) => OpenControls();
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        RefreshStartupOption();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(HandleMessage);
        _hotkeyRegistered = _source != null && Desktop.RegisterPartyHotkey(_source.Handle);
        if (!_hotkeyRegistered)
            HotkeyLabel.Text = "Shortcut unavailable · Use the tray";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_exiting || Dispatcher.HasShutdownStarted)
            return;

        e.Cancel = true;
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _exiting = true;
        if (_saveTimer.IsEnabled)
            SaveSettings();
        SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        _overlay?.Dispose();
        _audio.Dispose();
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Icon?.Dispose();
        _tray.Dispose();
        base.OnClosed(e);
    }

    private void ExitPartyTime()
    {
        _exiting = true;
        StopParty();
        if (_source is not null)
        {
            if (_hotkeyRegistered)
                Desktop.UnregisterHotKey(_source.Handle, Desktop.PartyHotkey);
            _source.RemoveHook(HandleMessage);
        }
        Application.Current.Shutdown();
    }

    internal void OpenControls()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void RefreshStartupOption()
    {
        try
        {
            StartupToggle.IsChecked = WindowsStartup.IsEnabled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Debug.WriteLine(ex);
            StartupToggle.IsEnabled = false;
            ShowStartupError("PartyTime couldn’t read your Windows startup setting.");
        }
    }

    private void StartupClicked(object sender, RoutedEventArgs e)
    {
        bool enabled = StartupToggle.IsChecked == true;
        try
        {
            WindowsStartup.SetEnabled(enabled);
            StartupError.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            Debug.WriteLine(ex);
            StartupToggle.IsChecked = !enabled;
            ShowStartupError(ex is InvalidOperationException ? ex.Message : "PartyTime couldn’t change your Windows startup setting.");
        }
    }

    private void ShowStartupError(string message)
    {
        StartupError.Text = message;
        StartupError.Visibility = Visibility.Visible;
    }

    private void ToggleClicked(object sender, RoutedEventArgs e) => ToggleParty();

    private void ToggleParty()
    {
        if (_overlay != null)
        {
            StopParty();
            return;
        }

        try
        {
            _overlay = new Overlay(_audio);
            _overlay.ApplySettings(_settings);
            StatusTitle.Text = "Party is on";
            StatusDot.Fill = Brushes.MediumSpringGreen;
            PartyButton.Content = "Stop _party";
            _trayToggle.Text = "Stop party";
            _tray.Text = "PartyTime · On";
            _audio.Start();
            RefreshColorSource();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            StopParty("Couldn’t start the desktop overlay.");
        }
    }

    private void StopParty(string detail = "Play music, then start the overlay.")
    {
        _overlay?.Dispose();
        _overlay = null;
        _audio.Dispose();
        StatusTitle.Text = "Party is off";
        StatusDetail.Text = detail;
        StatusDetail.ToolTip = null;
        StatusDot.Fill = Brushes.SlateGray;
        PartyButton.Content = "Start _party";
        _trayToggle.Text = "Start party";
        _tray.Text = "PartyTime · Off";
        RefreshColorSource();
    }

    private void SetAudioStatus(string detail)
    {
        StatusDetail.Text = detail;
        StatusDetail.ToolTip = detail;
    }

    private void RestoreControls()
    {
        SelectChoice(StyleChoices, _settings.Style);
        SelectChoice(FocusChoices, _settings.Focus);
        SelectChoice(SourceChoices, _settings.ColorSource);
        SelectChoice(PaletteChoices, _settings.Theme);
        IntensitySlider.Value = _settings.Intensity;
        SizeSlider.Value = _settings.Size;
        ResponseSlider.Value = _settings.Response;
        BlendSlider.Value = _settings.ScreenBlend;
        GlowToggle.IsChecked = _settings.EdgeGlow;
        AnimateToggle.IsChecked = _settings.AnimateColors;
        LowHzBox.Text = _settings.LowHz.ToString(CultureInfo.InvariantCulture);
        HighHzBox.Text = _settings.HighHz.ToString(CultureInfo.InvariantCulture);
        FirstColorBox.Text = _settings.FirstColor;
        SecondColorBox.Text = _settings.SecondColor;
    }

    private static void SelectChoice<T>(Panel choices, T selected) where T : struct, Enum
        => choices.Children.OfType<RadioButton>().First(option => (string)option.Tag == selected.ToString()).IsChecked = true;

    private static T ReadChoice<T>(Panel choices) where T : struct, Enum
        => Enum.Parse<T>((string)choices.Children.OfType<RadioButton>().First(option => option.IsChecked == true).Tag);

    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;

        bool validLow = int.TryParse(LowHzBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int low);
        bool validHigh = int.TryParse(HighHzBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int high);
        bool validRange = validLow && validHigh && FrequencyRange.IsValid(low, high);
        bool validFirst = VisualizerSettings.TryReadColor(FirstColorBox.Text, out Color first);
        bool validSecond = VisualizerSettings.TryReadColor(SecondColorBox.Text, out Color second);
        RangeError.Visibility = validRange ? Visibility.Collapsed : Visibility.Visible;
        ColorError.Visibility = validFirst && validSecond ? Visibility.Collapsed : Visibility.Visible;

        var next = _settings with
        {
            Style = ReadChoice<VisualStyle>(StyleChoices),
            Intensity = (int)IntensitySlider.Value,
            Size = (int)SizeSlider.Value,
            EdgeGlow = GlowToggle.IsChecked == true,
            Focus = ReadChoice<FrequencyFocus>(FocusChoices),
            LowHz = validRange ? low : _settings.LowHz,
            HighHz = validRange ? high : _settings.HighHz,
            Response = (int)ResponseSlider.Value,
            Theme = ReadChoice<ColorTheme>(PaletteChoices),
            ColorSource = ReadChoice<ColorSource>(SourceChoices),
            ScreenBlend = (int)BlendSlider.Value,
            FirstColor = validFirst ? VisualizerSettings.ColorHex(first) : _settings.FirstColor,
            SecondColor = validSecond ? VisualizerSettings.ColorHex(second) : _settings.SecondColor,
            AnimateColors = AnimateToggle.IsChecked == true
        };
        if (next == _settings)
            return;

        _settings = next;
        RefreshDescriptions();
        ApplySettings();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void RefreshDescriptions()
    {
        IntensityValue.Text = $"{_settings.Intensity}%";
        SizeValue.Text = $"{_settings.Size}%";
        ResponseValue.Text = $"{_settings.Response}%";
        BlendValue.Text = $"{_settings.ScreenBlend}%";
        StyleDescription.Text = _settings.Style switch
        {
            VisualStyle.Orbit => "A pulsing ring with radial spectrum bars in the center.",
            VisualStyle.CenterBars => "A mirrored equalizer across the middle of the screen.",
            VisualStyle.Wave => "A smooth spectrum ribbon across the middle of the screen.",
            VisualStyle.None => _settings.EdgeGlow ? "Edge glow and beat ripple only." : "Turn on edge glow for a border-only effect.",
            _ => "Spectrum bars along the top and bottom, with a clear center."
        };

        RangePanel.Visibility = _settings.Focus == FrequencyFocus.Custom ? Visibility.Visible : Visibility.Collapsed;
        var range = _settings.GetRange();
        FocusDescription.Text = _settings.Focus switch
        {
            FrequencyFocus.Bass => "Kick drums, basslines and low-end hits · 40–250 Hz",
            FrequencyFocus.Vocals => "Emphasizes common vocal frequencies · 300–3,400 Hz",
            FrequencyFocus.Mids => "Instruments and melodies · 250–2,000 Hz",
            FrequencyFocus.Treble => "Cymbals, hi-hats and bright details · 2,000–13,000 Hz",
            FrequencyFocus.Custom => $"Active range · {range.Low:N0}–{range.High:N0} Hz",
            _ => "Responds to the whole mix, with extra weight on bass hits."
        };

        PalettePanel.Visibility = _settings.ColorSource == ColorSource.Screen ? Visibility.Collapsed : Visibility.Visible;
        CustomColorsPanel.Visibility = _settings.Theme == ColorTheme.Custom ? Visibility.Visible : Visibility.Collapsed;
        BlendPanel.Visibility = _settings.ColorSource == ColorSource.Mix ? Visibility.Visible : Visibility.Collapsed;

        VisualizerSettings.TryReadColor(_settings.FirstColor, out Color first);
        VisualizerSettings.TryReadColor(_settings.SecondColor, out Color second);
        FirstSwatch.Background = new SolidColorBrush(first);
        SecondSwatch.Background = new SolidColorBrush(second);
    }

    private void ApplySettings()
    {
        _audio.Focus = _settings.GetRange();
        _overlay?.ApplySettings(_settings);
        Preview.ApplySettings(_settings);
        RefreshColorSource();
    }

    private void RefreshColorSource()
    {
        SourceDescription.Text = _settings.ColorSource switch
        {
            ColorSource.Mix when _settings.ScreenBlend == 0 => "At 0%, Mix uses only your selected palette.",
            ColorSource.Screen or ColorSource.Mix when _overlay is { CanSampleScreen: false }
                => "Screen sampling is unavailable. Using your selected palette.",
            ColorSource.Screen => "Uses colors from your primary display while the party is on.",
            ColorSource.Mix => "Blends your palette with colors from your primary display.",
            _ => "Use a preset or choose your own two colors."
        };

        bool usesScreenColors = _settings.ColorSource == ColorSource.Screen
            || (_settings.ColorSource == ColorSource.Mix && _settings.ScreenBlend > 0);
        CycleDescription.Text = usesScreenColors
            ? "Moves colors through the overlay. Screen colors still update when cycling is off."
            : "Moves palette colors through the overlay. Turn it off to keep each part the same color.";
    }

    private void SaveSettings()
    {
        _saveTimer.Stop();
        _settings.Save();
    }

    private void PickColor(object sender, RoutedEventArgs e)
    {
        bool first = ReferenceEquals(sender, FirstSwatch);
        VisualizerSettings.TryReadColor(first ? _settings.FirstColor : _settings.SecondColor, out Color color);
        using var picker = new Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B)
        };
        if (picker.ShowDialog(new DialogOwner(new WindowInteropHelper(this).Handle)) != Forms.DialogResult.OK)
            return;

        var selected = Color.FromRgb(picker.Color.R, picker.Color.G, picker.Color.B);
        (first ? FirstColorBox : SecondColorBox).Text = VisualizerSettings.ColorHex(selected);
    }

    private sealed class DialogOwner(IntPtr handle) : Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_exiting || _overlay == null)
                return;

            try
            {
                _overlay.RebuildScreens();
                RefreshColorSource();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                StopParty("The display layout changed. Start the party again.");
            }
        });
    }

    private IntPtr HandleMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Desktop.HotkeyMessage && wParam == Desktop.PartyHotkey)
        {
            handled = true;
            ToggleParty();
        }
        return IntPtr.Zero;
    }
}
