using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace Btl.Whiteboard.Client;

public sealed partial class MainWindow
{
    private void CanvasViewportGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCanvasViewportClip();
        ApplyCanvasViewportTransform();
    }

    private void ApplyCanvasViewportTransform()
    {
        ClampCanvasPanToViewport();
        drawingCanvasViewportTransform.ScaleX = canvasZoom;
        drawingCanvasViewportTransform.ScaleY = canvasZoom;
        drawingCanvasViewportTransform.TranslateX = canvasPanX;
        drawingCanvasViewportTransform.TranslateY = canvasPanY;
    }

    private void UpdateCanvasViewportClip()
    {
        double viewportWidth = Math.Max(0, CanvasViewportGrid.ActualWidth);
        double viewportHeight = Math.Max(0, CanvasViewportGrid.ActualHeight);
        canvasViewportClipGeometry.Rect = new Rect(0, 0, viewportWidth, viewportHeight);
    }

    private void ClampCanvasPanToViewport()
    {
        double viewportWidth = Math.Max(0, CanvasViewportGrid.ActualWidth);
        double viewportHeight = Math.Max(0, CanvasViewportGrid.ActualHeight);
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return;
        }

        double contentWidth = Math.Max(1, DrawingCanvas.ActualWidth);
        double contentHeight = Math.Max(1, DrawingCanvas.ActualHeight);
        double scaledWidth = contentWidth * canvasZoom;
        double scaledHeight = contentHeight * canvasZoom;

        if (scaledWidth <= viewportWidth)
        {
            canvasPanX = (viewportWidth - scaledWidth) / 2.0;
        }
        else
        {
            double minPanX = viewportWidth - scaledWidth;
            canvasPanX = Math.Clamp(canvasPanX, minPanX, 0);
        }

        if (scaledHeight <= viewportHeight)
        {
            canvasPanY = (viewportHeight - scaledHeight) / 2.0;
        }
        else
        {
            double minPanY = viewportHeight - scaledHeight;
            canvasPanY = Math.Clamp(canvasPanY, minPanY, 0);
        }
    }

    private void ResetCanvasViewport()
    {
        canvasZoom = DefaultCanvasZoom;
        canvasPanX = 0;
        canvasPanY = 0;
        ApplyCanvasViewportTransform();
        UpdateToolStatusText();
    }

    private static double ClampCanvasZoom(double zoom)
    {
        return Math.Clamp(zoom, MinCanvasZoom, MaxCanvasZoom);
    }

    private string GetCanvasZoomText()
    {
        return $"Zoom {Math.Round(canvasZoom * 100.0, 0).ToString(CultureInfo.InvariantCulture)}%";
    }
}