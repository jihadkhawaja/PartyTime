using Avalonia.Interactivity;
using PartyTime.Settings;
using PartyTime.Visualization;

namespace PartyTime.Views;

public partial class ControlWindow
{
    private void InitializeImmersionControls()
    {
        MusicEffectPicker.ItemsSource = new[] { "None", "Spectrum waterfall", "Warp tunnel", "Stage beams" };
        MusicReactionPicker.ItemsSource = ReactionLabels;
        Preview.GetAmbientColors = () => _overlay?.AmbientScreenColors;
    }

    private void RestoreImmersionControls()
    {
        MusicEffectPicker.SelectedIndex = (int)_settings.MusicEffect;
        MusicReactionPicker.SelectedIndex = (int)_settings.MusicReaction;
        MusicStrengthSlider.Value = _settings.MusicStrength;
        AmbientLightingToggle.IsChecked = _settings.AmbientLighting;
        AmbientScreenColorsToggle.IsChecked = _settings.AmbientScreenColors;
        CinemaVignetteToggle.IsChecked = _settings.CinemaVignette;
        CinemaStrengthSlider.Value = _settings.CinemaStrength;
    }

    private void RefreshImmersionDescriptions()
    {
        MusicEffectControls.IsVisible = _settings.MusicEffect != MusicEffect.None;
        MusicEffectDescription.Text = _settings.MusicEffect switch
        {
            MusicEffect.SpectrumWaterfall => "Shows the last 1.7 seconds of the spectrum as receding ridges.",
            MusicEffect.WarpTunnel => "Rippling rings move outward and speed up with the music.",
            MusicEffect.StageBeams => "Five spotlights react to frequencies from bass to treble.",
            _ => ""
        };
        MusicStrengthLabel.Text = $"Strength · {_settings.MusicStrength}%";
        AmbientColorControls.IsVisible = _settings.AmbientLighting;
        CinemaStrengthControls.IsVisible = _settings.AmbientLighting || _settings.CinemaVignette;
        CinemaStrengthLabel.Text = $"Strength · {_settings.CinemaStrength}%";
    }

    private void RefreshAmbientColorStatus()
    {
        AmbientColorStatus.Text = !_settings.AmbientScreenColors ? "Uses the palette selected in Colors."
            : !DesktopCapture.IsSupported || _overlay is { CanSampleScreen: false } ? "Screen colors are unavailable. Using your palette."
            : _overlay == null ? "Start the party to sample colors from each display."
            : "Samples each display separately and uses the palette when colors are unavailable.";
    }

    private void PreviewLiveChanged(object? sender, RoutedEventArgs e)
    {
        if (_ready)
            RefreshPreviewMode();
    }

    private void RefreshPreviewMode()
    {
        PreviewLiveToggle.IsEnabled = _overlay != null;
        Preview.SetLiveAudio(_overlay != null && PreviewLiveToggle.IsChecked == true ? () => _audio.Latest : null);
    }
}
