using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PartyTime.Settings;
using PartyTime.Visualization;

namespace PartyTime.Views;

public partial class ControlWindow
{
    private bool _editingImage;
    private bool _pickingImageColor;
    private static readonly string[] ReactionLabels =
        ["Follow focus", "Full mix", "Low · 40–250 Hz", "Mid · 250–2,000 Hz", "High · 2,000–13,000 Hz"];

    private void InitializeCustomizationControls()
    {
        foreach (var combo in new[] { BarsReaction, CircleReaction, BorderReaction, EffectsReaction, ImageReaction, ShapesReaction, CursorReaction })
            combo.ItemsSource = ReactionLabels;
        ImagePlacement.ItemsSource = new[] { "Sticker", "Image halo", "Floating images", "Replace dots / sparks" };
        foreach (var slider in new[] { ImageSize, ImageOpacity, ImageCount, ImageX, ImageY, ImageKeyTolerance })
        {
            slider.PreviewMouseLeftButtonUp += ImageSliderCommitted;
            slider.KeyUp += ImageSliderKeyUp;
        }
    }

    private OverlayImageSettings[] SelectedImages
    {
        get
        {
            var ids = ImageList.SelectedItems.Cast<OverlayImageSettings>().Select(image => image.Id).ToHashSet();
            return _settings.Images.Where(image => ids.Contains(image.Id)).ToArray();
        }
    }

    private OverlayImageSettings? SelectedImage => SelectedImages.FirstOrDefault();

    private void RefreshImageList(IEnumerable<string>? selectedIds = null, bool loadEditor = true)
    {
        var ids = (selectedIds ?? SelectedImages.Select(image => image.Id)).ToHashSet();
        _editingImage = true;
        try
        {
            ImageList.SelectedItems.Clear();
            ImageList.ItemsSource = _settings.Images;
            foreach (var image in _settings.Images.Where(image => ids.Contains(image.Id)))
                ImageList.SelectedItems.Add(image);
            if (ImageList.SelectedItems.Count == 0 && _settings.Images.Length > 0)
                ImageList.SelectedIndex = 0;
        }
        finally { _editingImage = false; }
        AddImageButton.IsEnabled = _settings.Images.Length < 16;
        SelectAllImagesButton.IsEnabled = _settings.Images.Length > 0;
        RemoveImageButton.IsEnabled = SelectedImage != null;
        if (loadEditor)
            LoadImageEditor();
    }

    private void ImageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_editingImage)
            LoadImageEditor();
    }

    private void LoadImageEditor()
    {
        var selected = SelectedImages;
        var image = selected.FirstOrDefault();
        ImageEditor.Visibility = image == null ? Visibility.Collapsed : Visibility.Visible;
        RemoveImageButton.IsEnabled = image != null;
        _pickingImageColor = false;
        if (image == null)
        {
            ImagePreview.Source = null;
            ImageSelectionSummary.Text = "No images selected.";
            RemoveImageButton.Content = "Remove image";
            return;
        }
        _editingImage = true;
        try
        {
            ImageEnabled.IsChecked = Mixed(selected, item => item.Enabled) ? null : image.Enabled;
            ImageName.IsEnabled = selected.Length == 1;
            ImageNameLabel.Text = selected.Length == 1 ? "Image name" : "Select one image to rename it";
            ImageName.Text = selected.Length == 1 ? image.Name : "";
            ImagePlacement.SelectedIndex = Mixed(selected, item => item.Layout) ? -1 : (int)image.Layout;
            ImageReaction.SelectedIndex = Mixed(selected, item => item.Reaction) ? -1 : (int)image.Reaction;
            ImageSize.Value = image.Size;
            ImageOpacity.Value = image.Opacity;
            ImageCount.Value = image.Count;
            ImageX.Value = image.X;
            ImageY.Value = image.Y;
            ImageChromaKey.IsChecked = Mixed(selected, item => item.ChromaKeyEnabled) ? null : image.ChromaKeyEnabled;
            ImageKeyColor.Text = Mixed(selected, item => item.ChromaKeyColor) ? "" : image.ChromaKeyColor;
            ImageKeyTolerance.Value = image.ChromaKeyTolerance;
            ImageKeyColorError.Visibility = Visibility.Collapsed;
        }
        finally { _editingImage = false; }
        RefreshImageDescriptions(image);
        ImageError.Text = OverlayImageStore.Load(image.Path) == null
            ? "This image is unavailable. Remove it and add the PNG again." : "";
        ImageError.Visibility = ImageError.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ImageSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _editingImage)
            return;
        var selected = SelectedImages;
        if (selected.Length == 0)
            return;
        bool validKeyColor = VisualizerSettings.TryReadColor(ImageKeyColor.Text, out var keyColor);
        if (ReferenceEquals(sender, ImageKeyColor))
            ImageKeyColorError.Visibility = validKeyColor ? Visibility.Collapsed : Visibility.Visible;
        // Apply only the edited field; other values may differ across the selection.
        Func<OverlayImageSettings, OverlayImageSettings>? update = (sender as FrameworkElement)?.Name switch
        {
            nameof(ImageName) when selected.Length == 1 => image => image with { Name = string.IsNullOrWhiteSpace(ImageName.Text) ? "Image" : ImageName.Text },
            nameof(ImageEnabled) when ImageEnabled.IsChecked != null => image => image with { Enabled = ImageEnabled.IsChecked == true },
            nameof(ImagePlacement) when ImagePlacement.SelectedIndex >= 0 => image => image with { Layout = (ImageLayout)ImagePlacement.SelectedIndex },
            nameof(ImageReaction) when ImageReaction.SelectedIndex >= 0 => image => image with { Reaction = (AudioReaction)ImageReaction.SelectedIndex },
            nameof(ImageSize) => image => image with { Size = (int)ImageSize.Value },
            nameof(ImageOpacity) => image => image with { Opacity = (int)ImageOpacity.Value },
            nameof(ImageCount) => image => image with { Count = (int)ImageCount.Value },
            nameof(ImageX) => image => image with { X = (int)ImageX.Value },
            nameof(ImageY) => image => image with { Y = (int)ImageY.Value },
            nameof(ImageChromaKey) when ImageChromaKey.IsChecked != null => image => image with { ChromaKeyEnabled = ImageChromaKey.IsChecked == true },
            nameof(ImageKeyColor) when validKeyColor => image => image with { ChromaKeyColor = VisualizerSettings.ColorHex(keyColor) },
            nameof(ImageKeyTolerance) => image => image with { ChromaKeyTolerance = (int)ImageKeyTolerance.Value },
            _ => null
        };
        if (update == null)
            return;
        var ids = selected.Select(image => image.Id).ToHashSet();
        var next = _settings.Images.Select(image => ids.Contains(image.Id) ? update(image) : image).ToArray();
        if (next.SequenceEqual(_settings.Images))
            return;
        _settings = _settings with { Images = next };
        if (ReferenceEquals(sender, ImageName))
            RefreshImageList(loadEditor: false);
        RefreshImageDescriptions(SelectedImage!);
        SaveImageChanges();
    }

    private static bool Mixed<T>(OverlayImageSettings[] selected, Func<OverlayImageSettings, T> value)
        => selected.Select(value).Distinct().Skip(1).Any();

    private void ImageSliderCommitted(object sender, MouseButtonEventArgs e) => ImageSettingsChanged(sender, e);

    private void ImageSliderKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            ImageSettingsChanged(sender, e);
    }

    private void SelectAllImagesClicked(object sender, RoutedEventArgs e) => ImageList.SelectAll();

    private void ImageListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ImageList.SelectAll();
            e.Handled = true;
        }
    }

    private void RefreshImageDescriptions(OverlayImageSettings image)
    {
        var selected = SelectedImages;
        string State<T>(Func<OverlayImageSettings, T> value) => Mixed(selected, value) ? "Mixed · showing " : "";
        ImageSelectionSummary.Text = selected.Length == 1 ? $"1 image selected · Preview: {image.Name}"
            : $"{selected.Length} images selected · Preview: {image.Name}. Changes apply to all selected images. A dash or Mixed means values differ; sliders show the preview image's value.";
        RemoveImageButton.Content = selected.Length > 1 ? "Remove selected" : "Remove image";
        ImageEnabled.Content = selected.Length > 1 ? "Show selected images" : "Show this image";
        ImageSizeLabel.Text = $"Size · {State(item => item.Size)}{image.Size}% of the screen's shorter side";
        ImageOpacityLabel.Text = $"Opacity · {State(item => item.Opacity)}{image.Opacity}%";
        ImageCountLabel.Text = $"Copies · {State(item => item.Count)}{image.Count}";
        ImageXLabel.Text = $"Horizontal position · {State(item => item.X)}{image.X}%";
        ImageYLabel.Text = $"Vertical position · {State(item => item.Y)}{image.Y}%";
        ImageKeyToleranceLabel.Text = $"Color tolerance · {State(item => item.ChromaKeyTolerance)}{image.ChromaKeyTolerance}%";
        ChromaKeyControls.IsEnabled = selected.Any(item => item.ChromaKeyEnabled);
        ImageKeyColorMixed.Visibility = Mixed(selected, item => item.ChromaKeyColor) ? Visibility.Visible : Visibility.Collapsed;
        if (!ChromaKeyControls.IsEnabled)
            _pickingImageColor = false;
        VisualizerSettings.TryReadColor(image.ChromaKeyColor, out var keyColor);
        ImageKeySwatch.Background = new SolidColorBrush(keyColor);
        RefreshImagePreview(image);
        ImageCountPanel.Visibility = selected.Any(item => item.Layout != ImageLayout.Sticker) ? Visibility.Visible : Visibility.Collapsed;
        ImagePositionPanel.Visibility = selected.Any(item => item.Layout is ImageLayout.Sticker or ImageLayout.Halo)
            ? Visibility.Visible : Visibility.Collapsed;
        ImagePlacementHelp.Text = Mixed(selected, item => item.Layout) ? "Mixed placements. Choosing a placement applies it to all selected images." : image.Layout switch
        {
            ImageLayout.Halo => "Repeats images around a circle. Images at the same position alternate in list order, with space reserved for each copy.",
            ImageLayout.Scatter => "Spreads floating copies into separate slots across each display, alternating images in list order.",
            ImageLayout.ReplaceDots => "Replaces the particles in Visuals → Dot halo and Sparks. Enable either effect to see them. Images alternate in separate slots, each using its own audio range.",
            _ => "Places one image at the chosen position. Stickers sharing a position are arranged side by side. Each pulses with its selected frequencies."
        };
    }

    private void RefreshImagePreview(OverlayImageSettings image)
    {
        ImagePreview.Source = _pickingImageColor ? OverlayImageStore.Load(image.Path) : OverlayImageStore.Load(image);
        ImagePreview.Cursor = _pickingImageColor ? Cursors.Cross : Cursors.Arrow;
        PickImageColorButton.Content = _pickingImageColor ? "Cancel picker" : "Pick from image";
        ImagePreviewHint.Text = _pickingImageColor
            ? SelectedImages.Length > 1 ? "Showing the original. Click a color to use for all selected images."
                : "Showing the original. Click the color you want to make transparent."
            : SelectedImages.Length > 1 ? "Preview of the first selected image. Color choices apply to the selection."
            : image.ChromaKeyEnabled ? "Preview with color removal applied. Pick from image to choose another color."
            : "The checkerboard shows transparent areas. Enable Remove a color to pick a background color.";
    }

    private void PickImageColorClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedImage is not { } image)
            return;
        _pickingImageColor = !_pickingImageColor;
        RefreshImagePreview(image);
        if (_pickingImageColor)
            ImagePreview.BringIntoView();
    }

    private void ImagePreviewClicked(object sender, MouseButtonEventArgs e)
    {
        if (!_pickingImageColor || SelectedImage is not { } image || OverlayImageStore.Load(image.Path) is not { } original)
            return;
        var color = PickPreviewColor(original, e.GetPosition(ImagePreview), ImagePreview.ActualWidth, ImagePreview.ActualHeight);
        if (color == null)
            return;
        e.Handled = true;
        if (color.Value.A == 0)
        {
            ImagePreviewHint.Text = "That pixel is already transparent. Pick a visible color.";
            return;
        }
        _pickingImageColor = false;
        ImageKeyColor.Text = VisualizerSettings.ColorHex(color.Value);
        RefreshImagePreview(SelectedImage ?? image);
    }

    internal static Color? PickPreviewColor(BitmapSource original, Point point, double width, double height)
    {
        double scale = Math.Min(width / original.PixelWidth, height / original.PixelHeight);
        if (scale <= 0)
            return null;
        double x = (point.X - (width - original.PixelWidth * scale) / 2) / scale;
        double y = (point.Y - (height - original.PixelHeight * scale) / 2) / scale;
        if (x < 0 || y < 0 || x >= original.PixelWidth || y >= original.PixelHeight)
            return null;
        return OverlayImageStore.ReadPixel(original, (int)x, (int)y);
    }

    private void AddImageClicked(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Add overlay images",
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|PNG images (*.png)|*.png|JPEG images (*.jpg;*.jpeg)|*.jpg;*.jpeg|Bitmap images (*.bmp)|*.bmp",
            Multiselect = true,
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true)
            return;
        var images = _settings.Images.ToList();
        var errors = new List<string>();
        var addedIds = new List<string>();
        foreach (string path in picker.FileNames)
        {
            if (images.Count >= 16)
            {
                errors.Add("You can add up to 16 images. Remove an image to add another.");
                break;
            }
            try
            {
                var image = new OverlayImageSettings { Name = Path.GetFileNameWithoutExtension(path), Path = OverlayImageStore.Import(path) };
                images.Add(image);
                addedIds.Add(image.Id);
            }
            catch (Exception ex) when (OverlayImageStore.IsImageError(ex))
            {
                errors.Add($"Couldn’t add {Path.GetFileName(path)}. Choose a readable PNG, JPEG, or BMP smaller than 20 MB.");
            }
        }
        _settings = _settings with { Images = images.ToArray() };
        RefreshImageList(addedIds.Count > 0 ? addedIds : null);
        ImageError.Text = string.Join(Environment.NewLine, errors);
        ImageError.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SaveImageChanges();
    }

    private void RemoveImageClicked(object sender, RoutedEventArgs e)
    {
        var ids = SelectedImages.Select(image => image.Id).ToHashSet();
        if (ids.Count == 0)
            return;
        _settings = _settings with { Images = _settings.Images.Where(item => !ids.Contains(item.Id)).ToArray() };
        RefreshImageList();
        ImageError.Visibility = Visibility.Collapsed;
        SaveImageChanges();
    }

    private void SaveImageChanges()
    {
        ApplySettings();
        _saveTimer.Stop();
        _saveTimer.Start();
    }
}
