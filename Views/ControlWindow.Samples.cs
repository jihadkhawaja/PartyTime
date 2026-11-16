using System;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PartyTime.Settings;
using PartyTime.Visualization;

namespace PartyTime.Views;

public partial class ControlWindow
{
    private DispatcherTimer? _samplePreviewTimer;
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
        SampleBlendPicker.ItemsSource = new[] { "Normal", "Additive", "Screen", "Multiply", "Difference", "Negative" };
        SampleReactionPicker.ItemsSource = new[] { "Follow focus", "Full mix", "Low · bass", "Mid · instruments", "High · treble" };
        _samplePreviewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        Opened += (_, _) => _samplePreviewTimer.Start();
        _samplePreviewTimer.Tick += (_, _) => RefreshSamplePreview();
        SamplePreviewHost.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (SamplePreviewHost.IsVisible)
            {
                RefreshSamplePreview();
                _samplePreviewTimer.Start();
            }
            else
            {
                _samplePreviewTimer.Stop();
                SamplePreview.Source = null;
            }
        };
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
        SamplePreview.Source = _settings.ScreenSamplesEnabled ? _overlay?.ScreenSamplePreview : null;
        SampleStatus.Text = !DesktopCapture.IsSupported ? DesktopCapture.UnavailableReason
            : !_settings.ScreenSamplesEnabled ? "Enable screen samples to add a beat-synced screenshot layer."
            : _overlay == null ? "Start the party and play audio to capture samples. The preview shows your primary display."
            : _settings.SampleOpacity == 0 ? "Overlay opacity is 0%. Raise it to capture and show samples."
            : _overlay.ScreenSampleStatus;
    }
}
