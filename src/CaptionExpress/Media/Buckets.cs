namespace CaptionExpress.Media;

public sealed record Bucket(string Name, int Width, int Height)
{
    public double Ratio => (double)Width / Height;
    public override string ToString() => $"{Name}  ·  {Width} × {Height}";
}

public static class Buckets
{
    // Built-in LTX 2.3 aspect-ratio buckets.
    public static readonly IReadOnlyList<Bucket> All =
    [
        new("12:5", 1536, 640),
        new("7:4", 1344, 768),
        new("20:13", 1280, 832),
        new("9:7", 1152, 896),
        new("1:1", 1024, 1024),
        new("7:9", 896, 1152),
        new("13:20", 832, 1280),
        new("4:7", 768, 1344),
        new("5:12", 640, 1536)
    ];

    public static Bucket Nearest(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Source dimensions must be positive.");

        double ratio = (double)width / height;
        return All.MinBy(bucket => Math.Abs(Math.Log(ratio / bucket.Ratio)))!;
    }

    public static CropRectangle SourceCrop(int width, int height, Bucket bucket, double x, double y)
    {
        x = Math.Clamp(x, 0, 1);
        y = Math.Clamp(y, 0, 1);
        double sourceRatio = (double)width / height;
        double cropWidth = sourceRatio > bucket.Ratio ? height * bucket.Ratio : width;
        double cropHeight = sourceRatio > bucket.Ratio ? height : width / bucket.Ratio;
        return new CropRectangle((width - cropWidth) * x, (height - cropHeight) * y, cropWidth, cropHeight);
    }
}

public readonly record struct CropRectangle(double X, double Y, double Width, double Height);
