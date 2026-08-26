using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SftpExplorerWinUI.Helpers;
using SftpExplorerWinUI.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace SftpExplorerWinUI.Controls;

public sealed partial class AppearancePicker : UserControl
{
    private static readonly IReadOnlyList<(int CodePoint, string Name)> PreferredGlyphs = new (int, string)[]
    {
        (0xE965, "MediaStorageTower"),
        (0xEC05, "NetworkTower"),
        (0xEDA2, "HardDrive"),
        (0xEB77, "GatewayRouter"),
        (0xEDA3, "NetworkAdapter"),
        (0xE839, "Ethernet"),
        (0xE968, "Network"),
        (0xEC27, "MyNetwork"),
        (0xE969, "StorageNetworkWireless"),
        (0xE96A, "StorageTape"),
        (0xE753, "Cloud"),
        (0xEBD3, "CloudDownload"),
        (0xE774, "Globe"),
        (0xE909, "World"),
        (0xEB41, "Website"),
        (0xE703, "Connect"),
        (0xE705, "VPN"),
        (0xE90F, "Repair"),
        (0xE912, "Manage"),
        (0xE8AF, "Remote"),
        (0xE977, "PC1"),
        (0xEC4E, "ThisPC"),
        (0xE772, "Devices"),
        (0xE756, "CommandPrompt"),
        (0xE943, "Code"),
        (0xEC7A, "DeveloperTools"),
        (0xE950, "Component"),
        (0xE9F3, "Process"),
        (0xE9D9, "Diagnostic"),
        (0xE95E, "Health"),
        (0xE72E, "Lock"),
        (0xEA18, "Shield"),
        (0xEB95, "Certificate"),
        (0xE7EF, "Admin"),
        (0xE8D7, "Permissions"),
        (0xE83D, "DefenderApp"),
        (0xE8B7, "Folder"),
        (0xE838, "FolderOpen"),
        (0xE8CE, "MapDrive"),
        (0xE895, "Sync"),
        (0xE896, "Download"),
        (0xE898, "Upload")
    };
    private static readonly IReadOnlyDictionary<int, string> PreferredGlyphNames =
        PreferredGlyphs.ToDictionary(option => option.CodePoint, option => option.Name);
    private static readonly IReadOnlyList<GlyphOption> GlyphOptions = BuildGlyphOptions();

    public static readonly IReadOnlyList<string> Palette = new[]
    {
        "#0078D4", // Blue
        "#107C10", // Green
        "#D83B01", // Orange
        "#E81123", // Red
        "#5C2D91", // Purple
        "#008272", // Teal
        "#69797E"  // Gray
    };

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header),
        typeof(string),
        typeof(AppearancePicker),
        new PropertyMetadata("", OnHeaderChanged));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(AppearancePicker),
        new PropertyMetadata(ConnectionAppearanceDefaults.ConnectionGlyph, OnGlyphChanged));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color),
        typeof(string),
        typeof(AppearancePicker),
        new PropertyMetadata(ConnectionAppearanceDefaults.DefaultColor, OnColorChanged));

    private bool _updatingGlyphCode;

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Color
    {
        get => (string)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public AppearancePicker()
    {
        InitializeComponent();
        GlyphGrid.ItemsSource = GlyphOptions;
        GlyphCodeBox.Header = LocalizationHelper.GetString("SymbolCodeLabel");
        GlyphCodeBox.PlaceholderText = LocalizationHelper.GetString("SymbolCodePlaceholder");

        foreach (var paletteColor in Palette)
        {
            var colorCircle = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = CreateBrush(paletteColor),
                BorderBrush = CreateBrush(paletteColor),
                BorderThickness = new Thickness(0)
            };
            var colorButton = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = colorCircle,
                Tag = paletteColor
            };
            colorButton.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
            colorButton.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Colors.Transparent);
            colorButton.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Colors.Transparent);
            colorButton.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Colors.Transparent);
            AutomationProperties.SetName(colorButton, paletteColor);
            ToolTipService.SetToolTip(colorButton, paletteColor);
            colorButton.Click += ColorButton_Click;
            colorButton.PointerEntered += ColorButton_PointerEntered;
            colorButton.PointerExited += ColorButton_PointerExited;
            ColorPanel.Children.Add(colorButton);
        }

        UpdateHeader();
        UpdateGlyph();
        UpdateColor();
    }

    private static void OnHeaderChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((AppearancePicker)sender).UpdateHeader();
    }

    private static void OnGlyphChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((AppearancePicker)sender).UpdateGlyph();
    }

    private static void OnColorChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((AppearancePicker)sender).UpdateColor();
    }

    private void UpdateHeader()
    {
        if (HeaderText != null)
            HeaderText.Text = Header;
    }

    private void UpdateGlyph()
    {
        if (GlyphPreview == null)
            return;

        var glyph = string.IsNullOrEmpty(Glyph)
            ? ConnectionAppearanceDefaults.ConnectionGlyph
            : Glyph;
        GlyphPreview.Glyph = glyph;

        if (GlyphCodeBox != null && !_updatingGlyphCode)
        {
            _updatingGlyphCode = true;
            GlyphCodeBox.Text = char.ConvertToUtf32(glyph, 0).ToString("X4", CultureInfo.InvariantCulture);
            _updatingGlyphCode = false;
        }
    }

    private void UpdateColor()
    {
        if (GlyphPreview == null || ColorPanel == null)
            return;

        var selectedColor = Palette.Contains(Color, StringComparer.OrdinalIgnoreCase)
            ? Color
            : ConnectionAppearanceDefaults.DefaultColor;
        GlyphPreview.Foreground = CreateBrush(selectedColor);

        foreach (var button in ColorPanel.Children.OfType<Button>())
        {
            var isSelected = string.Equals(button.Tag as string, selectedColor, StringComparison.OrdinalIgnoreCase);
            if (button.Content is Border colorCircle)
            {
                colorCircle.BorderThickness = new Thickness(isSelected ? 3 : 0);
                colorCircle.BorderBrush = isSelected
                    ? new SolidColorBrush(Colors.White)
                    : colorCircle.Background;
            }
        }
    }

    private void GlyphCodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingGlyphCode)
            return;

        var codeText = GlyphCodeBox.Text.Trim().TrimStart('U', 'u', '+');
        if (!int.TryParse(codeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint) ||
            codePoint < 0 ||
            codePoint > 0x10FFFF ||
            codePoint is >= 0xD800 and <= 0xDFFF)
        {
            return;
        }

        Glyph = char.ConvertFromUtf32(codePoint);
    }

    private void GlyphGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GlyphOption option)
        {
            Glyph = option.Glyph;
            GlyphFlyout.Hide();
        }
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
            Color = color;
    }

    private void ColorButton_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        SetColorCircleSize(sender, 24);
    }

    private void ColorButton_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        SetColorCircleSize(sender, 28);
    }

    private static void SetColorCircleSize(object sender, double size)
    {
        if (sender is not Button { Content: Border colorCircle })
            return;

        colorCircle.Width = size;
        colorCircle.Height = size;
        colorCircle.CornerRadius = new CornerRadius(size / 2);
    }

    private static SolidColorBrush CreateBrush(string color)
    {
        var value = color.TrimStart('#');
        if (uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
        {
            if (value.Length == 6)
                argb |= 0xFF000000;

            return new SolidColorBrush(Windows.UI.Color.FromArgb(
                (byte)(argb >> 24),
                (byte)(argb >> 16),
                (byte)(argb >> 8),
                (byte)argb));
        }

        return new SolidColorBrush(Colors.DodgerBlue);
    }

    private static IReadOnlyList<GlyphOption> BuildGlyphOptions()
    {
        var codePoints = Enumerable.Range(0xE700, 0xF8FF - 0xE700 + 1).ToArray();
        var characters = new string(codePoints.Select(codePoint => (char)codePoint).ToArray());
        var glyphIndices = new ushort[characters.Length];
        var deviceContext = CreateCompatibleDC(IntPtr.Zero);
        var font = CreateFont(
            -20,
            0,
            0,
            0,
            400,
            0,
            0,
            0,
            1,
            0,
            0,
            0,
            0,
            "Segoe Fluent Icons");

        if (deviceContext == IntPtr.Zero || font == IntPtr.Zero)
        {
            if (font != IntPtr.Zero)
                DeleteObject(font);
            if (deviceContext != IntPtr.Zero)
                DeleteDC(deviceContext);
            return CreateGlyphOptions(OrderCodePoints(codePoints));
        }

        var previousFont = SelectObject(deviceContext, font);
        try
        {
            var result = GetGlyphIndices(
                deviceContext,
                characters,
                characters.Length,
                glyphIndices,
                1);
            if (result == uint.MaxValue)
                return CreateGlyphOptions(codePoints);

            return CreateGlyphOptions(OrderCodePoints(
                codePoints.Where((_, index) => glyphIndices[index] != ushort.MaxValue)));
        }
        finally
        {
            SelectObject(deviceContext, previousFont);
            DeleteObject(font);
            DeleteDC(deviceContext);
        }
    }

    private static IReadOnlyList<GlyphOption> CreateGlyphOptions(IEnumerable<int> codePoints)
    {
        return codePoints.Select(codePoint => new GlyphOption
        {
            Glyph = char.ConvertFromUtf32(codePoint),
            Code = codePoint.ToString("X4", CultureInfo.InvariantCulture),
            Name = PreferredGlyphNames.TryGetValue(codePoint, out var name)
                ? $"{name} (U+{codePoint:X4})"
                : $"U+{codePoint:X4}"
        }).ToList();
    }

    private static IEnumerable<int> OrderCodePoints(IEnumerable<int> codePoints)
    {
        var availableCodePoints = codePoints.ToHashSet();
        foreach (var preferredGlyph in PreferredGlyphs)
        {
            if (availableCodePoints.Remove(preferredGlyph.CodePoint))
                yield return preferredGlyph.CodePoint;
        }

        foreach (var codePoint in codePoints)
        {
            if (availableCodePoints.Contains(codePoint))
                yield return codePoint;
        }
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFont(
        int height,
        int width,
        int escapement,
        int orientation,
        int weight,
        uint italic,
        uint underline,
        uint strikeOut,
        uint characterSet,
        uint outputPrecision,
        uint clipPrecision,
        uint quality,
        uint pitchAndFamily,
        string faceName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetGlyphIndices(
        IntPtr deviceContext,
        string text,
        int count,
        [Out] ushort[] glyphIndices,
        uint flags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr graphicsObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);
}

public sealed class GlyphOption
{
    public required string Glyph { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
}
