using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using PartyTime.Audio;
using PartyTime.Interop;
using PartyTime.Settings;
using PartyTime.Visualization;

namespace PartyTime.Views;

public partial class ControlWindow : Window
{
    private readonly TrayIcon _tray;
    private readonly NativeMenuItem _trayToggle;
    private readonly SystemAudio _audio;
    private readonly DispatcherTimer _saveTimer;
    private VisualizerSettings _settings;
    private readonly bool _persistSettings;
    private Overlay? _overlay;
    private IDisposable? _hotkey;
    private bool _exiting;
    private bool _ready;

    public ControlWindow() : this(VisualizerSettings.Load()) { }

    internal ControlWindow(VisualizerSettings settings, bool persistSettings = true)
    {
        _settings = settings;
        _persistSettings = persistSettings;
        InitializeComponent();
        _audio = new SystemAudio();
        _audio.StatusChanged += SetAudioStatus;
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => SaveSettings();
        Preview.GetScreenColors = () => _overlay?.ScreenColors;
        InitializeCustomizationControls();
        InitializeMotionControls();
        InitializeSampleControls();
        RestoreControls();
        RefreshDescriptions();
        ApplySettings();
        _ready = true;
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Open controls");
        open.Click += (_, _) => OpenControls();
        menu.Add(open);
        _trayToggle = new NativeMenuItem("Start party");
        _trayToggle.Click += (_, _) => ToggleParty();
        menu.Add(_trayToggle);
        menu.Add(new NativeMenuItemSeparator());
        var exit = new NativeMenuItem("Exit PartyTime");
        exit.Click += (_, _) => ExitPartyTime();
        menu.Add(exit);
        _tray = new TrayIcon { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PartyTime/Assets/PartyTime-v2.ico"))),
            Menu = menu, ToolTipText = "PartyTime · Off", IsVisible = true };
        _tray.Clicked += (_, _) => OpenControls();
        if (_persistSettings && Application.Current is { } app)
        {
            var icons = TrayIcon.GetIcons(app);
            if (icons == null) TrayIcon.SetIcons(app, icons = new TrayIcons());
            icons.Add(_tray);
        }
        Screens.Changed += OnDisplaysChanged;
        Opened += (_, _) =>
        {
            if (!_persistSettings) return;
            _hotkey ??= Desktop.WatchPartyShortcut(ToggleParty);
            HotkeyLabel.Text = Desktop.ShortcutDescription;
        };
        if (!_persistSettings) return;
        RefreshStartupOption();
        CloseBehaviorLabel.Text = CanHideToTray ? "Closes to the tray" : "Close to quit";
        if (!CanHideToTray) StartupDescription.Text = "Starts PartyTime at sign-in. Controls stay visible so the app remains reachable without a tray host.";
    }

    private bool CanHideToTray => (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) && _tray.NativeMenuExporter != null;

    internal void ConfigureStartup(string[] args)
    {
        // Always keep controls reachable on desktops without a system tray host.
        Opened += (_, _) =>
        {
            if (args.Contains(AppStartup.TrayArgument) && CanHideToTray) Hide();
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_exiting && CanHideToTray)
        {
            e.Cancel = true;
            Hide();
        }
        else if (!_exiting) { e.Cancel = true; _exiting = true; Dispatcher.UIThread.Post(ExitPartyTime); }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _exiting = true;
        _samplePreviewTimer?.Stop();
        _samplePreviewClock?.Dispose();
        SetSamplePreview(null);
        _samplePreviewLayer.Dispose();
        if (_saveTimer.IsEnabled) SaveSettings();
        Screens.Changed -= OnDisplaysChanged;
        _hotkey?.Dispose();
        _overlay?.Dispose();
        _audio.Dispose();
        if (Application.Current is { } app) TrayIcon.GetIcons(app)?.Remove(_tray);
        _tray.Dispose();
        base.OnClosed(e);
    }

    internal void ExitPartyTime()
    {
        _exiting = true;
        StopParty();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    internal void OpenControls()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void RefreshStartupOption()
    {
        try { StartupToggle.IsChecked = AppStartup.IsEnabled(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            StartupToggle.IsEnabled = false;
            ShowStartupError(ex.Message);
        }
    }

    private void StartupClicked(object? sender, RoutedEventArgs e)
    {
        bool enabled = StartupToggle.IsChecked == true;
        try { AppStartup.SetEnabled(enabled); StartupError.IsVisible = false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            StartupToggle.IsChecked = !enabled;
            ShowStartupError(ex.Message);
        }
    }

    private void ShowStartupError(string message) { StartupError.Text = message; StartupError.IsVisible = true; }
    private void ToggleClicked(object? sender, RoutedEventArgs e) => ToggleParty();

    private void ToggleParty()
    {
        if (_overlay != null) { StopParty(); return; }
        try
        {
            _overlay = new Overlay(_audio, this);
            _overlay.ApplySettings(_settings);
            StatusTitle.Text = "Party is on";
            StatusDot.Fill = Brushes.MediumSpringGreen;
            PartyButton.Content = "Stop _party";
            _trayToggle.Header = "Stop party";
            _tray.ToolTipText = "PartyTime · On";
            _audio.Start();
            UpdateSampleCaptureExclusion();
            RefreshColorSource();
            UpdateSamplePreviewTimer();
        }
        catch (Exception ex) { Debug.WriteLine(ex); StopParty(ex.Message); }
    }

    private void StopParty(string detail = "Play music, then start the overlay.")
    {
        SetSamplePreview(null);
        _overlay?.Dispose();
        _overlay = null;
        UpdateSampleCaptureExclusion();
        _audio.Stop();
        StatusTitle.Text = "Party is off";
        StatusDetail.Text = detail;
        ToolTip.SetTip(StatusDetail, detail);
        StatusDot.Fill = Brushes.SlateGray;
        PartyButton.Content = "Start _party";
        _trayToggle.Header = "Start party";
        _tray.ToolTipText = "PartyTime · Off";
        RefreshColorSource();
        RefreshSamplePreview();
        UpdateSamplePreviewTimer();
    }

    private void SetAudioStatus(string detail)
    {
        StatusDetail.Text = detail;
        ToolTip.SetTip(StatusDetail, detail);
    }

    private void RestoreControls()
    {
        SelectChoice(StyleChoices, _settings.Style);
        SelectChoice(CircleChoices, _settings.Circle);
        SelectChoice(BorderChoices, _settings.Border);
        SelectChoice(OverlayChoices, _settings.Overlay);
        SelectChoice(FrameRateChoices, _settings.FrameRate);
        SelectChoice(FocusChoices, _settings.Focus);
        SelectChoice(SourceChoices, _settings.ColorSource);
        SelectChoice(PaletteChoices, _settings.Theme);
        IntensitySlider.Value = _settings.Intensity;
        SizeSlider.Value = _settings.Size;
        ResponseSlider.Value = _settings.Response;
        BlendSlider.Value = _settings.ScreenBlend;
        AnimateToggle.IsChecked = _settings.AnimateColors;
        LowHzBox.Text = _settings.LowHz.ToString(CultureInfo.InvariantCulture);
        HighHzBox.Text = _settings.HighHz.ToString(CultureInfo.InvariantCulture);
        FirstColorBox.Text = _settings.FirstColor;
        SecondColorBox.Text = _settings.SecondColor;
        BarsReaction.SelectedIndex = (int)_settings.BarsReaction;
        CircleReaction.SelectedIndex = (int)_settings.CircleReaction;
        BorderReaction.SelectedIndex = (int)_settings.BorderReaction;
        EffectsReaction.SelectedIndex = (int)_settings.EffectsReaction;
        RestoreMotionControls();
        RestoreSampleControls();
        RefreshImageList();
    }

    private static void SelectChoice<T>(Panel choices, T selected) where T : struct, Enum
        => choices.Children.OfType<RadioButton>().First(option => (string)option.Tag! == selected.ToString()).IsChecked = true;

    private static T ReadChoice<T>(Panel choices) where T : struct, Enum
        => Enum.Parse<T>((string)choices.Children.OfType<RadioButton>().First(option => option.IsChecked == true).Tag!);

    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;

        bool validLow = int.TryParse(LowHzBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int low);
        bool validHigh = int.TryParse(HighHzBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int high);
        bool validRange = validLow && validHigh && FrequencyRange.IsValid(low, high);
        bool validFirst = VisualizerSettings.TryReadColor(FirstColorBox.Text, out Color first);
        bool validSecond = VisualizerSettings.TryReadColor(SecondColorBox.Text, out Color second);
        RangeError.IsVisible = validRange ? false : true;
        ColorError.IsVisible = validFirst && validSecond ? false : true;

        var next = _settings with
        {
            Style = ReadChoice<VisualStyle>(StyleChoices),
            Circle = ReadChoice<CircleStyle>(CircleChoices),
            Border = ReadChoice<BorderStyle>(BorderChoices),
            Overlay = ReadChoice<OverlayStyle>(OverlayChoices),
            BarsReaction = (AudioReaction)BarsReaction.SelectedIndex,
            CircleReaction = (AudioReaction)CircleReaction.SelectedIndex,
            BorderReaction = (AudioReaction)BorderReaction.SelectedIndex,
            EffectsReaction = (AudioReaction)EffectsReaction.SelectedIndex,
            ShapesEnabled = ShapesToggle.IsChecked == true,
            Shape = (ShapeKind)ShapePicker.SelectedIndex,
            ShapeMotion = (ShapeMotion)ShapeMotionPicker.SelectedIndex,
            FilledShapes = FilledShapesToggle.IsChecked == true,
            ShapeCount = (int)ShapeCountSlider.Value,
            ShapeSize = (int)ShapeSizeSlider.Value,
            ShapeOpacity = (int)ShapeOpacitySlider.Value,
            ShapesReaction = (AudioReaction)ShapesReaction.SelectedIndex,
            CursorTrailEnabled = CursorTrailToggle.IsChecked == true,
            CursorTrail = (CursorTrailStyle)CursorStylePicker.SelectedIndex,
            CursorShape = (ShapeKind)CursorShapePicker.SelectedIndex,
            CursorTrailLength = (int)CursorLengthSlider.Value,
            CursorTrailSize = (int)CursorSizeSlider.Value,
            CursorReaction = (AudioReaction)CursorReaction.SelectedIndex,
            ScreenSamplesEnabled = ScreenSamplesToggle.IsChecked == true,
            SampleBlend = (ScreenSampleBlend)SampleBlendPicker.SelectedIndex,
            SampleReaction = (AudioReaction)SampleReactionPicker.SelectedIndex,
            SampleBeats = (int)SampleBeatsSlider.Value,
            SampleOpacity = (int)SampleOpacitySlider.Value,
            SampleZoom = (int)SampleZoomSlider.Value,
            SampleFade = (int)SampleFadeSlider.Value,
            FrameRate = ReadChoice<OverlayFrameRate>(FrameRateChoices),
            Intensity = (int)IntensitySlider.Value,
            Size = (int)SizeSlider.Value,
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
        RefreshMotionDescriptions();
        RefreshSampleDescriptions();
        IntensityValue.Text = $"{_settings.Intensity}%";
        SizeValue.Text = $"{_settings.Size}%";
        ResponseValue.Text = $"{_settings.Response}%";
        BlendValue.Text = $"{_settings.ScreenBlend}%";
        RangePanel.IsVisible = _settings.Focus == FrequencyFocus.Custom ? true : false;
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

        PalettePanel.IsVisible = _settings.ColorSource == ColorSource.Screen ? false : true;
        CustomColorsPanel.IsVisible = _settings.Theme == ColorTheme.Custom ? true : false;
        BlendPanel.IsVisible = _settings.ColorSource == ColorSource.Mix ? true : false;

        VisualizerSettings.TryReadColor(_settings.FirstColor, out Color first);
        VisualizerSettings.TryReadColor(_settings.SecondColor, out Color second);
        FirstSwatch.Background = new SolidColorBrush(first);
        SecondSwatch.Background = new SolidColorBrush(second);
    }

    private void ApplySettings()
    {
        _audio.Focus = _settings.GetRange();
        SetSamplePreview(null);
        _overlay?.ApplySettings(_settings);
        UpdateSampleCaptureExclusion();
        Preview.ApplySettings(_settings);
        RefreshColorSource();
        UpdateSamplePreviewTimer();
    }

    private void RefreshColorSource()
    {
        SourceDescription.Text = _settings.ColorSource switch
        {
            ColorSource.Mix when _settings.ScreenBlend == 0 => "At 0%, Mix uses only your selected palette.",
            ColorSource.Screen or ColorSource.Mix when !DesktopCapture.IsSupported
                => "Screen colors are unavailable on this desktop. Using your selected palette.",
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
        if (_persistSettings)
            _settings.Save();
    }

    private async void PickColor(object sender, RoutedEventArgs e)
    {
        bool first = ReferenceEquals(sender, FirstSwatch);
        VisualizerSettings.TryReadColor(first ? _settings.FirstColor : _settings.SecondColor, out Color color);
        var picker = new ColorPicker { Color = color, IsAlphaEnabled = false };
        var accept = new Button { Content = "Use color", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(16), Children = { picker, accept } };
        var dialog = new Window { Title = "Choose color", Width = 420, Height = 520, Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        accept.Click += (_, _) => dialog.Close(true);
        if (await dialog.ShowDialog<bool>(this))
            (first ? FirstColorBox : SecondColorBox).Text = VisualizerSettings.ColorHex(picker.Color);
    }

    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        if (_exiting || _overlay == null) return;
        try { _overlay.RebuildScreens(); RefreshColorSource(); }
        catch (Exception ex) { Debug.WriteLine(ex); StopParty("The display layout changed. Start the party again."); }
    }
}
