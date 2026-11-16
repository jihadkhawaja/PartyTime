using System;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PartyTime.Settings;
using PartyTime.Visualization;
using Avalonia.Controls;
using System.Diagnostics;

namespace PartyTime.Views;

public partial class ControlWindow
{
    private DispatcherTimer? _samplePreviewTimer;
    private CompositionClock? _samplePreviewClock;
    private readonly ScreenSampleLayer _samplePreviewLayer = new();
    private bool _sampleControlsExcluded;

    private void UpdateSampleCaptureExclusion()
    {
        bool exclude = _settings.ScreenSamplesEnabled && _overlay != null;
        if (exclude != _sampleControlsExcluded
            && Interop.Desktop.SetCaptureExcluded(this, exclude))
            _sampleControlsExcluded = exclude;
    }

    private void InitializeSampleControls()
    {
        SamplePreview.Child = _samplePreviewLayer;
        SampleBlendPicker.ItemsSource = new[] { "Normal", "Additive", "Screen", "Multiply", "Difference", "Negative" };
        SampleReactionPicker.ItemsSource = new[] { "Follow focus", "Full mix", "Low · bass", "Mid · instruments", "High · treble" };
        _samplePreviewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        Opened += (_, _) => UpdateSamplePreviewTimer();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty || e.Property == WindowStateProperty) UpdateSamplePreviewTimer();
        };
        _samplePreviewTimer.Tick += (_, _) => RefreshSamplePreview();
        SamplePreviewHost.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            UpdateSamplePreviewTimer();
        };
    }

    private void SettingsTabSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, SettingsTabs)) UpdateSamplePreviewTimer();
    }
    private void UpdateSamplePreviewTimer()
    {
        if (_samplePreviewTimer == null) return;
        if (IsVisible && WindowState != WindowState.Minimized && SettingsTabs.SelectedIndex == 2)
        {
            _samplePreviewTimer.Start();
            if (_overlay != null && _settings.ScreenSamplesEnabled) _samplePreviewClock ??= new CompositionClock(this, RenderSamplePreview);
            else { _samplePreviewClock?.Dispose(); _samplePreviewClock = null; }
            RefreshSamplePreview();
        }
        else { _samplePreviewTimer.Stop(); _samplePreviewClock?.Dispose(); _samplePreviewClock = null; SetSamplePreview(null); }
    }
    private void SetSamplePreview(ScreenSampleFrame? frame)
    {
        if (frame != null) { _samplePreviewLayer.Width = frame.Held.Size.Width; _samplePreviewLayer.Height = frame.Held.Size.Height; }
        var request = _overlay?.SampleRequest;
        double zoom = request == null ? 1 : 1 + request.Settings.SampleZoom / 100.0 * request.Pulse(Stopwatch.GetTimestamp());
        _samplePreviewLayer.ShowFrame(frame, _settings.SampleBlend, zoom, frame == null ? 0 : 1);
    }
    private void RenderSamplePreview()
    {
        SetSamplePreview(IsVisible && WindowState != WindowState.Minimized && SettingsTabs.SelectedIndex == 2 && SamplePreviewHost.IsEffectivelyVisible && _settings.ScreenSamplesEnabled ? _overlay?.AcquirePrimarySample() : null);
    }

    private void RestoreSampleControls()
    {
        ScreenSamplesToggle.IsChecked = _settings.ScreenSamplesEnabled;
        SampleBlendPicker.SelectedIndex = (int)_settings.SampleBlend;
        SampleReactionPicker.SelectedIndex = (int)_settings.SampleReaction;
        SampleBeatsSlider.Value = _settings.SampleBeats;
        SampleOpacitySlider.Value = _settings.SampleOpacity;
        SampleZoomSlider.Value = _settings.SampleZoom;
        SampleFadeSlider.Value = _settings.SampleFade;
    }

    private void RefreshSampleDescriptions()
    {
        ScreenSamplesPanel.IsVisible = _settings.ScreenSamplesEnabled ? true : false;
        SampleBeatsLabel.Text = _settings.SampleBeats == 1 ? "New screenshot · every beat" : $"New screenshot · every {_settings.SampleBeats} beats";
        SampleOpacityLabel.Text = $"Overlay opacity · {_settings.SampleOpacity}%";
        SampleZoomLabel.Text = $"Beat zoom · {_settings.SampleZoom}%";
        SampleFadeLabel.Text = $"Fade after each beat · {_settings.SampleFade} ms";
        SampleBlendDescription.Text = _settings.SampleBlend switch
        {
            ScreenSampleBlend.Additive => "Adds the held sample to the live desktop for a bright flash.",
            ScreenSampleBlend.Screen => "Lightens the desktop with a softer, luminous blend.",
            ScreenSampleBlend.Multiply => "Multiplies the sample with the desktop for a darker echo.",
            ScreenSampleBlend.Difference => "Shows the difference between the held sample and the live desktop.",
            ScreenSampleBlend.Negative => "Inverts the colors of the held screenshot.",
            _ => "Layers the held screenshot over the desktop as a fading echo."
        };
        RefreshSamplePreview();
    }

    private void RefreshSamplePreview()
    {
        RenderSamplePreview();
        SampleStatus.Text = !DesktopCapture.IsSupported ? DesktopCapture.UnavailableReason
            : !_settings.ScreenSamplesEnabled ? "Enable screen samples to add a beat-synced screenshot layer."
            : _overlay == null ? "Start the party and play audio to capture samples. The preview shows your primary display."
            : _settings.SampleOpacity == 0 ? "Overlay opacity is 0%. Raise it to capture and show samples."
            : _overlay.ScreenSampleStatus;
    }
}
