using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CaptionExpress.Media;

namespace CaptionExpress;

public sealed class CropEditor : Control
{
    private Bitmap? _frame;
    private Bucket? _bucket;
    private double _x = 0.5;
    private double _y = 0.5;
    private bool _dragging;

    public event Action<double, double>? CropChanged;

    public void SetFrame(Bitmap? frame) { _frame = frame; InvalidateVisual(); }
    public void SetBucket(Bucket? bucket) { _bucket = bucket; InvalidateVisual(); }
    public void SetPosition(double x, double y)
    {
        _x = Math.Clamp(x, 0, 1);
        _y = Math.Clamp(y, 0, 1);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Black, null, Bounds);
        if (_frame is null) return;
        var image = ImageRect(_frame, Bounds);
        context.DrawImage(_frame, new Rect(0, 0, _frame.PixelSize.Width, _frame.PixelSize.Height), image);
        if (_bucket is null) return;

        var crop = Buckets.SourceCrop(_frame.PixelSize.Width, _frame.PixelSize.Height, _bucket, _x, _y);
        var frameCrop = new Rect(
            image.X + crop.X / _frame.PixelSize.Width * image.Width,
            image.Y + crop.Y / _frame.PixelSize.Height * image.Height,
            crop.Width / _frame.PixelSize.Width * image.Width,
            crop.Height / _frame.PixelSize.Height * image.Height);
        var shade = new SolidColorBrush(Color.FromArgb(155, 0, 0, 0));
        context.DrawRectangle(shade, null, new Rect(image.X, image.Y, image.Width, frameCrop.Top - image.Y));
        context.DrawRectangle(shade, null, new Rect(image.X, frameCrop.Bottom, image.Width, image.Bottom - frameCrop.Bottom));
        context.DrawRectangle(shade, null, new Rect(image.X, frameCrop.Top, frameCrop.Left - image.X, frameCrop.Height));
        context.DrawRectangle(shade, null, new Rect(frameCrop.Right, frameCrop.Top, image.Right - frameCrop.Right, frameCrop.Height));
        context.DrawRectangle(null, new Pen(Brushes.White, 2), frameCrop);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_frame is null || _bucket is null) return;
        _dragging = true;
        e.Pointer.Capture(this);
        SetCropFromPointer(e.GetPosition(this));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) SetCropFromPointer(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void SetCropFromPointer(Point pointer)
    {
        if (_frame is null || _bucket is null) return;
        var image = ImageRect(_frame, Bounds);
        double px = Math.Clamp((pointer.X - image.X) / image.Width, 0, 1) * _frame.PixelSize.Width;
        double py = Math.Clamp((pointer.Y - image.Y) / image.Height, 0, 1) * _frame.PixelSize.Height;
        var crop = Buckets.SourceCrop(_frame.PixelSize.Width, _frame.PixelSize.Height, _bucket, 0, 0);
        double maxX = _frame.PixelSize.Width - crop.Width;
        double maxY = _frame.PixelSize.Height - crop.Height;
        _x = maxX > 0 ? Math.Clamp((px - crop.Width / 2) / maxX, 0, 1) : 0.5;
        _y = maxY > 0 ? Math.Clamp((py - crop.Height / 2) / maxY, 0, 1) : 0.5;
        InvalidateVisual();
        CropChanged?.Invoke(_x, _y);
    }

    internal static Rect ImageRect(Bitmap frame, Rect bounds)
    {
        double scale = Math.Min(bounds.Width / frame.PixelSize.Width, bounds.Height / frame.PixelSize.Height);
        double width = frame.PixelSize.Width * scale;
        double height = frame.PixelSize.Height * scale;
        return new Rect(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height);
    }
}

public sealed class CropOutputPreview : Control
{
    private Bitmap? _frame;
    private Bucket? _bucket;
    private double _x = 0.5;
    private double _y = 0.5;

    public void Update(Bitmap? frame, Bucket? bucket, double x, double y)
    {
        _frame = frame;
        _bucket = bucket;
        _x = x;
        _y = y;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Black, null, Bounds);
        if (_frame is null || _bucket is null) return;
        var crop = Buckets.SourceCrop(_frame.PixelSize.Width, _frame.PixelSize.Height, _bucket, _x, _y);
        double scale = Math.Min(Bounds.Width / _bucket.Width, Bounds.Height / _bucket.Height);
        double width = _bucket.Width * scale;
        double height = _bucket.Height * scale;
        var destination = new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
        context.DrawImage(_frame, new Rect(crop.X, crop.Y, crop.Width, crop.Height), destination);
    }
}
