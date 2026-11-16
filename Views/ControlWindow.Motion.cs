using System.Linq;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Controls;
using PartyTime.Settings;

namespace PartyTime.Views;

public partial class ControlWindow
{
    private static readonly string[] ShapeLabels =
        ["Circles", "Squares", "Triangles", "Diamonds", "Stars", "Hexagons", "Hearts", "Crosses", "Pentagons", "Octagons", "Mixed shapes"];

    private void InitializeMotionControls()
    {
        ShapePicker.ItemsSource = ShapeLabels;
        CursorShapePicker.ItemsSource = ShapeLabels;
        ShapeMotionPicker.ItemsSource = new[] { "Float", "Orbit", "Rain", "Beat burst", "Spiral" };
        CursorStylePicker.ItemsSource = new[] { "Glow", "Ribbon", "Shapes" };
        PresetPicker.ItemsSource = VisualPresets.All.Select(preset => preset.Name).ToArray();
        PresetPicker.SelectedIndex = 0;
    }

    private void RestoreMotionControls()
    {
        ShapesToggle.IsChecked = _settings.ShapesEnabled;
        ShapePicker.SelectedIndex = (int)_settings.Shape;
        ShapeMotionPicker.SelectedIndex = (int)_settings.ShapeMotion;
        FilledShapesToggle.IsChecked = _settings.FilledShapes;
        ShapeCountSlider.Value = _settings.ShapeCount;
        ShapeSizeSlider.Value = _settings.ShapeSize;
        ShapeOpacitySlider.Value = _settings.ShapeOpacity;
        ShapesReaction.SelectedIndex = (int)_settings.ShapesReaction;
        CursorTrailToggle.IsChecked = _settings.CursorTrailEnabled;
        CursorStylePicker.SelectedIndex = (int)_settings.CursorTrail;
        CursorShapePicker.SelectedIndex = (int)_settings.CursorShape;
        CursorLengthSlider.Value = _settings.CursorTrailLength;
        CursorSizeSlider.Value = _settings.CursorTrailSize;
        CursorReaction.SelectedIndex = (int)_settings.CursorReaction;
    }

    private void RefreshMotionDescriptions()
    {
        ShapesPanel.IsVisible = _settings.ShapesEnabled ? true : false;
        CursorTrailPanel.IsVisible = _settings.CursorTrailEnabled ? true : false;
        CursorShapePanel.IsVisible = _settings.CursorTrail == CursorTrailStyle.Shapes ? true : false;
        ShapeCountLabel.Text = $"Shapes · {_settings.ShapeCount}";
        ShapeSizeLabel.Text = $"Shape size · {_settings.ShapeSize}%";
        ShapeOpacityLabel.Text = $"Shape opacity · {_settings.ShapeOpacity}%";
        CursorLengthLabel.Text = $"Trail lifetime · {_settings.CursorTrailLength} ms";
        CursorSizeLabel.Text = $"Trail size · {_settings.CursorTrailSize}";
    }

    private void PresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PresetPicker.SelectedIndex >= 0)
            PresetDescription.Text = VisualPresets.All[PresetPicker.SelectedIndex].Description;
    }

    private void ApplyPresetClicked(object sender, RoutedEventArgs e)
    {
        if (!_ready || PresetPicker.SelectedIndex < 0)
            return;
        _settings = VisualPresets.All[PresetPicker.SelectedIndex].Apply(_settings);
        _ready = false;
        try { RestoreControls(); }
        finally { _ready = true; }
        RefreshDescriptions();
        ApplySettings();
        _saveTimer.Stop();
        _saveTimer.Start();
    }
}
