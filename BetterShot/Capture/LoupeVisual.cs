using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace BetterShot.Capture;

internal sealed class LoupeVisual : Canvas
{
    internal const int Zoom = 8;
    internal const int SampleSize = 15;
    internal const double Diameter = SampleSize * Zoom;
    internal const double VisualWidth = 136;
    internal const double VisualHeight = 160;
    private const double ImageOffset = 8;
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(255, 210, 74));
    private readonly Image _image;
    private readonly Rectangle _vertical;
    private readonly Rectangle _horizontal;
    private readonly Rectangle _pixel;
    private readonly TextBlock _detail;
    private readonly Canvas _crosshairLayer;

    internal LoupeVisual()
    {
        Width = VisualWidth;
        Height = VisualHeight;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;

        var shadow = new Ellipse
        {
            Width = 126, Height = 126, Fill = Brushes.Black, Opacity = .65,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = .5, Color = Colors.Black }
        };
        SetLeft(shadow, 5); SetTop(shadow, 5); Children.Add(shadow);

        _image = new Image
        {
            Width = Diameter, Height = Diameter, Stretch = Stretch.Fill,
            Clip = new EllipseGeometry(new Point(Diameter / 2, Diameter / 2), Diameter / 2, Diameter / 2),
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        SetLeft(_image, ImageOffset); SetTop(_image, ImageOffset); Children.Add(_image);

        _crosshairLayer = new Canvas
        {
            Width = Diameter, Height = Diameter,
            Clip = new EllipseGeometry(new Point(Diameter / 2, Diameter / 2), Diameter / 2, Diameter / 2)
        };
        SetLeft(_crosshairLayer, ImageOffset); SetTop(_crosshairLayer, ImageOffset);
        _vertical = Crosshair(1, 30);
        _horizontal = Crosshair(30, 1);
        _pixel = new Rectangle
        {
            Width = Zoom, Height = Zoom, Fill = Brushes.Transparent, Stroke = Accent,
            StrokeThickness = 1, SnapsToDevicePixels = true
        };
        _crosshairLayer.Children.Add(_vertical);
        _crosshairLayer.Children.Add(_horizontal);
        _crosshairLayer.Children.Add(_pixel);
        Children.Add(_crosshairLayer);

        var innerRing = new Ellipse
        {
            Width = Diameter, Height = Diameter,
            Stroke = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), StrokeThickness = 1
        };
        SetLeft(innerRing, ImageOffset); SetTop(innerRing, ImageOffset); Children.Add(innerRing);
        var outerRing = new Ellipse
        {
            Width = 126, Height = 126, Stroke = new SolidColorBrush(Color.FromRgb(35, 35, 38)), StrokeThickness = 3
        };
        SetLeft(outerRing, 5); SetTop(outerRing, 5); Children.Add(outerRing);

        _detail = new TextBlock
        {
            Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI Semibold"), FontSize = 11,
            Text = "8×  ·  0 × 0", HorizontalAlignment = HorizontalAlignment.Center
        };
        var pill = new Border
        {
            Width = 112, Height = 25, Background = new SolidColorBrush(Color.FromArgb(238, 25, 25, 28)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12.5), Child = _detail, Padding = new Thickness(7, 4, 7, 3)
        };
        SetLeft(pill, 12); SetTop(pill, 135); Children.Add(pill);
    }

    internal bool UpdateSample(BitmapSource source, int cursorX, int cursorY, int selectionWidth, int selectionHeight)
    {
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0) return false;
        cursorX = Math.Clamp(cursorX, 0, source.PixelWidth - 1);
        cursorY = Math.Clamp(cursorY, 0, source.PixelHeight - 1);
        _image.Source = CreateCenteredSample(source, cursorX, cursorY);
        _image.Width = Diameter; _image.Height = Diameter;
        SetLeft(_image, ImageOffset); SetTop(_image, ImageOffset);
        const double pixelX = (SampleSize / 2) * Zoom;
        const double pixelY = (SampleSize / 2) * Zoom;
        SetLeft(_pixel, pixelX); SetTop(_pixel, pixelY);
        SetLeft(_vertical, pixelX + Zoom / 2 - .5); SetTop(_vertical, pixelY - 11);
        SetLeft(_horizontal, pixelX - 11); SetTop(_horizontal, pixelY + Zoom / 2 - .5);
        _detail.Text = $"8×  ·  {Math.Max(0, selectionWidth)} × {Math.Max(0, selectionHeight)}";
        return true;
    }

    private static BitmapSource CreateCenteredSample(BitmapSource source, int cursorX, int cursorY)
    {
        const int radius = SampleSize / 2;
        int left = Math.Max(0, cursorX - radius), top = Math.Max(0, cursorY - radius);
        int right = Math.Min(source.PixelWidth, cursorX + radius + 1);
        int bottom = Math.Min(source.PixelHeight, cursorY + radius + 1);
        int width = right - left, height = bottom - top;
        BitmapSource crop = new CroppedBitmap(source, new Int32Rect(left, top, width, height));
        if (crop.Format != PixelFormats.Bgra32)
            crop = new FormatConvertedBitmap(crop, PixelFormats.Bgra32, null, 0);
        var croppedPixels = new byte[width * height * 4];
        crop.CopyPixels(croppedPixels, width * 4, 0);
        var paddedPixels = new byte[SampleSize * SampleSize * 4];
        int originX = left - (cursorX - radius), originY = top - (cursorY - radius);
        for (int y = 0; y < SampleSize; y++)
        for (int x = 0; x < SampleSize; x++)
        {
            int sourceX = Math.Clamp(x - originX, 0, width - 1);
            int sourceY = Math.Clamp(y - originY, 0, height - 1);
            Buffer.BlockCopy(croppedPixels, (sourceY * width + sourceX) * 4,
                paddedPixels, (y * SampleSize + x) * 4, 4);
        }
        var result = BitmapSource.Create(SampleSize, SampleSize, 96, 96,
            PixelFormats.Bgra32, null, paddedPixels, SampleSize * 4);
        result.Freeze();
        return result;
    }

    private static Rectangle Crosshair(double width, double height) => new()
    {
        Width = width, Height = height, Fill = new SolidColorBrush(Color.FromArgb(230, 20, 20, 22)),
        SnapsToDevicePixels = true
    };
}
