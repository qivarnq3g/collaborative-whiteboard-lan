using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;

namespace Btl.Whiteboard.Client;

public sealed partial class MainWindow
{
    private void SelectDrawingTool(DrawingTool tool)
    {
        selectedDrawingTool = tool;
        UpdateToolToggleSelection();
        UpdateToolStatusText();
    }

    private void UpdateToolToggleSelection()
    {
        if (isUpdatingToolSelection)
        {
            return;
        }

        isUpdatingToolSelection = true;
        PencilToolToggleButton.IsChecked = selectedDrawingTool == DrawingTool.Pencil;
        LineToolToggleButton.IsChecked = selectedDrawingTool == DrawingTool.Line;
        RectangleToolToggleButton.IsChecked = selectedDrawingTool == DrawingTool.Rectangle;
        EllipseToolToggleButton.IsChecked = selectedDrawingTool == DrawingTool.Ellipse;
        EraserToolToggleButton.IsChecked = selectedDrawingTool == DrawingTool.Eraser;
        isUpdatingToolSelection = false;
    }

    private void UpdateToolStatusText()
    {
        if (CurrentToolTextBlock is null)
        {
            return;
        }

        string colorLabel = GetEffectiveStrokeColorHex();
        double thickness = GetEffectiveStrokeThickness();
        LayerState activeLayer = GetActiveLayerState();
        CurrentToolTextBlock.Text = $"Tool: {GetToolDisplayName(selectedDrawingTool)} | {colorLabel} | {thickness:0.#} px | {GetCanvasZoomText()} | {activeLayer.Name}";
    }

    private static string GetToolDisplayName(DrawingTool tool)
    {
        return tool switch
        {
            DrawingTool.Line => "Line",
            DrawingTool.Rectangle => "Rectangle",
            DrawingTool.Ellipse => "Ellipse",
            DrawingTool.Eraser => "Eraser",
            _ => "Pencil"
        };
    }

    private void ApplySelectedColorHex(string colorHex, bool syncComboSelection, bool trackRecent)
    {
        string normalized = NormalizeStrokeColorHex(colorHex);
        selectedStrokeColorHex = normalized;

        EnsureColorOptionExists(normalized);

        isUpdatingColorSelection = true;
        CustomColorTextBox.Text = normalized;
        if (syncComboSelection)
        {
            SyncColorSelectionFromHex(normalized);
        }

        SyncAdvancedColorPicker(normalized);

        isUpdatingColorSelection = false;
        if (trackRecent)
        {
            TrackRecentColor(normalized);
        }

        RefreshColorSwatchSelection();
        UpdateToolStatusText();
    }

    private void SyncAdvancedColorPicker(string colorHex)
    {
        if (!TryParseHexColor(colorHex, out Windows.UI.Color color))
        {
            return;
        }

        if (AdvancedColorPicker is null)
        {
            return;
        }

        AdvancedColorPicker.Color = color;
    }

    private void RefreshColorSwatchSelection()
    {
        Brush selectedBorderBrush = ResolveThemeBrush("ColorSwatchSelectedBorderBrush");
        Brush idleBorderBrush = ResolveThemeBrush("PanelStrokeBrush");
        string normalizedSelectedHex = NormalizeStrokeColorHex(selectedStrokeColorHex);

        foreach (Button swatchButton in EnumerateSwatchButtons())
        {
            string swatchHex = NormalizeStrokeColorHex(swatchButton.Tag as string);
            bool isSelected = string.Equals(swatchHex, normalizedSelectedHex, StringComparison.Ordinal);
            swatchButton.BorderBrush = isSelected ? selectedBorderBrush : idleBorderBrush;
            swatchButton.BorderThickness = isSelected ? new Thickness(1.5) : new Thickness(1);
        }
    }

    private IEnumerable<Button> EnumerateSwatchButtons()
    {
        foreach (Button swatchButton in ColorSwatchesPanel.Children.OfType<Button>())
        {
            yield return swatchButton;
        }

        foreach (Button swatchButton in RecentColorSwatchesPanel.Children.OfType<Button>())
        {
            yield return swatchButton;
        }
    }

    private void TrackRecentColor(string colorHex)
    {
        string normalized = NormalizeStrokeColorHex(colorHex);
        if (recentColors.Count > 0 && string.Equals(recentColors[0], normalized, StringComparison.Ordinal))
        {
            return;
        }

        int existingIndex = recentColors.FindIndex(existing => string.Equals(existing, normalized, StringComparison.Ordinal));
        if (existingIndex >= 0)
        {
            recentColors.RemoveAt(existingIndex);
        }

        recentColors.Insert(0, normalized);
        if (recentColors.Count > MaxRecentColors)
        {
            recentColors.RemoveRange(MaxRecentColors, recentColors.Count - MaxRecentColors);
        }

        RefreshRecentColorSwatches();
    }

    private void RefreshRecentColorSwatches()
    {
        RecentColorSwatchesPanel.Children.Clear();

        foreach (string recentColorHex in recentColors)
        {
            Button swatchButton = new()
            {
                Style = Application.Current.Resources["ColorSwatchButtonStyle"] as Style,
                Tag = recentColorHex,
                Background = TryParseHexColor(recentColorHex, out Windows.UI.Color swatchColor)
                    ? new SolidColorBrush(swatchColor)
                    : new SolidColorBrush(Colors.Transparent)
            };

            ToolTipService.SetToolTip(swatchButton, recentColorHex);
            swatchButton.Click += RecentColorSwatchButton_Click;
            RecentColorSwatchesPanel.Children.Add(swatchButton);
        }
    }

    private void RecentColorSwatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button recentColorButton || recentColorButton.Tag is not string colorHex)
        {
            return;
        }

        ApplySelectedColorHex(colorHex, syncComboSelection: true, trackRecent: true);
    }

    private void ApplyCustomColorFromInput()
    {
        if (!TryNormalizeUserHexColor(CustomColorTextBox.Text, out string normalized))
        {
            SetConnectionState("Error", "Invalid color code. Use #RRGGBB or #AARRGGBB.");
            return;
        }

        ApplySelectedColorHex(normalized, syncComboSelection: true, trackRecent: true);
    }

    private void EnsureColorOptionExists(string colorHex)
    {
        for (int index = 0; index < StrokeColorComboBox.Items.Count; index++)
        {
            if (StrokeColorComboBox.Items[index] is not ComboBoxItem item || item.Tag is not string optionColorHex)
            {
                continue;
            }

            if (string.Equals(NormalizeStrokeColorHex(optionColorHex), colorHex, StringComparison.Ordinal))
            {
                return;
            }
        }

        StrokeColorComboBox.Items.Add(new ComboBoxItem
        {
            Content = colorHex,
            Tag = colorHex
        });
    }

    private void SyncColorSelectionFromHex(string colorHex)
    {
        for (int index = 0; index < StrokeColorComboBox.Items.Count; index++)
        {
            if (StrokeColorComboBox.Items[index] is not ComboBoxItem item || item.Tag is not string optionColorHex)
            {
                continue;
            }

            if (!string.Equals(NormalizeStrokeColorHex(optionColorHex), colorHex, StringComparison.Ordinal))
            {
                continue;
            }

            StrokeColorComboBox.SelectedIndex = index;
            return;
        }
    }

    private string GetEffectiveStrokeColorHex()
    {
        if (selectedDrawingTool != DrawingTool.Eraser)
        {
            return NormalizeStrokeColorHex(selectedStrokeColorHex);
        }

        return TryGetCanvasBackgroundHex(out string canvasColorHex)
            ? canvasColorHex
            : "#FFFFFF";
    }

    private double GetEffectiveStrokeThickness()
    {
        if (selectedDrawingTool != DrawingTool.Eraser)
        {
            return NormalizeStrokeThickness(selectedStrokeThickness);
        }

        return NormalizeStrokeThickness(Math.Max(8, selectedStrokeThickness * 1.8));
    }

    private bool TryGetCanvasBackgroundHex(out string colorHex)
    {
        if (CanvasHost.Background is SolidColorBrush hostBrush)
        {
            colorHex = ToHexColor(hostBrush.Color);
            return true;
        }

        Brush fallbackBrush = ResolveThemeBrush("CanvasBackgroundBrush");
        if (fallbackBrush is SolidColorBrush solidFallbackBrush)
        {
            colorHex = ToHexColor(solidFallbackBrush.Color);
            return true;
        }

        colorHex = "#FFFFFF";
        return false;
    }

    private static string ToHexColor(Windows.UI.Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static bool IsShapeTool(DrawingTool tool)
    {
        return tool is DrawingTool.Line or DrawingTool.Rectangle or DrawingTool.Ellipse;
    }

    private Shape? CreateShapePreview(DrawingTool tool)
    {
        if (!IsShapeTool(tool))
        {
            return null;
        }

        Shape shape = tool switch
        {
            DrawingTool.Line => new Line(),
            DrawingTool.Rectangle => new Rectangle(),
            DrawingTool.Ellipse => new Ellipse(),
            _ => new Line()
        };

        shape.Stroke = CreateStrokeBrush(GetSafeUserName(), GetEffectiveStrokeColorHex());
        shape.StrokeThickness = GetEffectiveStrokeThickness();
        shape.StrokeStartLineCap = PenLineCap.Round;
        shape.StrokeEndLineCap = PenLineCap.Round;

        return shape;
    }

    private void UpdateShapePreview(Point start, Point end)
    {
        if (shapePreviewShape is null)
        {
            return;
        }

        shapePreviewShape.Stroke = CreateStrokeBrush(GetSafeUserName(), GetEffectiveStrokeColorHex());
        shapePreviewShape.StrokeThickness = GetEffectiveStrokeThickness();

        if (shapePreviewShape is Line line)
        {
            line.X1 = start.X;
            line.Y1 = start.Y;
            line.X2 = end.X;
            line.Y2 = end.Y;
            return;
        }

        double left = Math.Min(start.X, end.X);
        double top = Math.Min(start.Y, end.Y);
        double width = Math.Max(1, Math.Abs(end.X - start.X));
        double height = Math.Max(1, Math.Abs(end.Y - start.Y));

        Canvas.SetLeft(shapePreviewShape, left);
        Canvas.SetTop(shapePreviewShape, top);

        if (shapePreviewShape is Rectangle rectangle)
        {
            rectangle.Width = width;
            rectangle.Height = height;
            return;
        }

        if (shapePreviewShape is Ellipse ellipse)
        {
            ellipse.Width = width;
            ellipse.Height = height;
        }
    }

    private void RemoveShapePreview()
    {
        if (shapePreviewShape is null)
        {
            return;
        }

        DrawingCanvas.Children.Remove(shapePreviewShape);
        shapePreviewShape = null;
    }

    private static IReadOnlyList<Point> BuildShapeStrokePoints(Point start, Point end, DrawingTool tool)
    {
        if (CalculateDistance(start, end) < 1)
        {
            return Array.Empty<Point>();
        }

        return tool switch
        {
            DrawingTool.Line => new[]
            {
                start,
                end
            },
            DrawingTool.Rectangle => new[]
            {
                new Point(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y)),
                new Point(Math.Max(start.X, end.X), Math.Min(start.Y, end.Y)),
                new Point(Math.Max(start.X, end.X), Math.Max(start.Y, end.Y)),
                new Point(Math.Min(start.X, end.X), Math.Max(start.Y, end.Y)),
                new Point(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y))
            },
            DrawingTool.Ellipse => BuildEllipsePoints(start, end, 52),
            _ => Array.Empty<Point>()
        };
    }

    private async Task SendShapeStrokeAsync(IReadOnlyList<Point> points)
    {
        if (!session.IsConnected || points.Count < 2)
        {
            return;
        }

        LayerState activeLayer = GetActiveLayerState();
        if (!activeLayer.IsVisible || activeLayer.IsLocked)
        {
            return;
        }

        string strokeId = Guid.NewGuid().ToString("N");
        string userId = session.LocalSenderId;
        string userName = GetSafeUserName();
        string colorHex = GetEffectiveStrokeColorHex();
        double thickness = GetEffectiveStrokeThickness();
        string layerId = activeLayer.LayerId;
        CancellationToken strokeSendToken = BeginActiveStrokeSendSession();

        try
        {
            Point first = points[0];
            await session.SendStrokeStartAsync(
                strokeId,
                first.X,
                first.Y,
                userId,
                userName,
                colorHex,
                thickness,
                layerId,
                strokeSendToken).ConfigureAwait(false);

            for (int index = 1; index < points.Count - 1; index++)
            {
                Point point = points[index];
                await session.SendStrokePointAsync(
                    strokeId,
                    point.X,
                    point.Y,
                    userId,
                    userName,
                    colorHex,
                    thickness,
                    layerId,
                        strokeSendToken).ConfigureAwait(false);
            }

            Point last = points[^1];
            await session.SendStrokeEndAsync(
                strokeId,
                last.X,
                last.Y,
                userId,
                userName,
                colorHex,
                thickness,
                layerId,
                strokeSendToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetConnectionState("Error", $"Shape send failed: {exception.Message}");
        }
        finally
        {
            CancelActiveStrokeSendSession();
        }
    }

    private static IReadOnlyList<Point> BuildEllipsePoints(Point start, Point end, int segments)
    {
        double left = Math.Min(start.X, end.X);
        double top = Math.Min(start.Y, end.Y);
        double width = Math.Abs(end.X - start.X);
        double height = Math.Abs(end.Y - start.Y);

        if (width < 1 || height < 1)
        {
            return new[]
            {
                start,
                end
            };
        }

        double centerX = left + (width / 2.0);
        double centerY = top + (height / 2.0);
        double radiusX = width / 2.0;
        double radiusY = height / 2.0;

        List<Point> points = new(segments + 1);
        for (int index = 0; index <= segments; index++)
        {
            double angle = (Math.PI * 2.0 * index) / segments;
            points.Add(new Point(
                centerX + (radiusX * Math.Cos(angle)),
                centerY + (radiusY * Math.Sin(angle))));
        }

        return points;
    }

    private void UpdateCanvasPointerText(Point point)
    {
    }

    private string GetSelectedStrokeColorHex()
    {
        if (StrokeColorComboBox.SelectedItem is ComboBoxItem selected && selected.Tag is string colorHex)
        {
            return NormalizeStrokeColorHex(colorHex);
        }

        return NormalizeStrokeColorHex(DefaultStrokeColorHex);
    }

    private double GetSelectedStrokeThickness()
    {
        if (StrokeThicknessNumberBox is not null && !double.IsNaN(StrokeThicknessNumberBox.Value))
        {
            return NormalizeStrokeThickness(StrokeThicknessNumberBox.Value);
        }

        return NormalizeStrokeThickness(DefaultStrokeThickness);
    }


    private void StrokeColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isUpdatingColorSelection)
        {
            return;
        }

        ApplySelectedColorHex(GetSelectedStrokeColorHex(), syncComboSelection: false, trackRecent: true);
    }


    private void StrokeThicknessNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        selectedStrokeThickness = GetSelectedStrokeThickness();
        UpdateToolStatusText();
    }


    private void CustomColorTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        ApplyCustomColorFromInput();
    }


    private void ApplyCustomColorButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyCustomColorFromInput();
    }


    private void ColorSwatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button colorButton || colorButton.Tag is not string swatchHex)
        {
            return;
        }

        ApplySelectedColorHex(swatchHex, syncComboSelection: true, trackRecent: true);
    }


    private void AdvancedColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (isUpdatingColorSelection)
        {
            return;
        }

        string colorHex = ToHexColor(args.NewColor);
        ApplySelectedColorHex(colorHex, syncComboSelection: true, trackRecent: false);
    }


    private void AdvancedColorPickerFlyout_Closed(object sender, object e)
    {
        TrackRecentColor(selectedStrokeColorHex);
        RefreshColorSwatchSelection();
    }


    private void ToolToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toolButton)
        {
            return;
        }

        DrawingTool tool = toolButton.Name switch
        {
            nameof(LineToolToggleButton) => DrawingTool.Line,
            nameof(RectangleToolToggleButton) => DrawingTool.Rectangle,
            nameof(EllipseToolToggleButton) => DrawingTool.Ellipse,
            nameof(EraserToolToggleButton) => DrawingTool.Eraser,
            _ => DrawingTool.Pencil
        };

        SelectDrawingTool(tool);
    }





}
