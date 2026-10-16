namespace CaptionExpress.Media;

/// <summary>
/// A practical export recipe for one training family. Bucket lists are representative:
/// compatible dimensions are determined by the selected model's VAE, not by this list.
/// </summary>
public sealed record TrainingExportPreset(
    string Id,
    string Name,
    int SpatialMultiple,
    int TemporalFactor,
    int MinimumFrames,
    double DefaultFrameRate,
    IReadOnlyList<double> FrameRates,
    IReadOnlyList<Bucket> Buckets,
    VideoExportFormat DefaultVideoFormat)
{
    public static readonly TrainingExportPreset Ltx23 = new(
        "ltx-23", "LTX 2.3", 32, 8, 9, 24, [24], global::CaptionExpress.Media.Buckets.All,
        VideoExportFormat.H264Mp4);

    public static readonly IReadOnlyList<TrainingExportPreset> All =
        [Ltx23];

    public static TrainingExportPreset? FindById(string? id) =>
        All.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.Ordinal));

    public Bucket NearestBucket(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Source dimensions must be positive.");
        if (Buckets.Count == 0)
            throw new InvalidOperationException("The export preset has no automatic buckets.");
        double ratio = (double)width / height;
        double area = (double)width * height;
        return Buckets.MinBy(bucket =>
            (Math.Abs(Math.Log(ratio / bucket.Ratio)),
                Math.Abs(Math.Log(area / ((double)bucket.Width * bucket.Height)))))!;
    }

    public double ChooseFrameRate(double? sourceFrameRate)
    {
        if (FrameRates.Count == 0 || FrameRates.Any(rate => !double.IsFinite(rate) || rate <= 0))
            throw new InvalidOperationException("The export preset has no valid frame rates.");
        if (sourceFrameRate is not { } source || !double.IsFinite(source) || source <= 0)
            return DefaultFrameRate;
        return FrameRates.MinBy(rate => (Math.Abs(rate - source), Math.Abs(rate - DefaultFrameRate)));
    }

    public void ValidateBucket(Bucket bucket)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        if (SpatialMultiple <= 0 || bucket.Width <= 0 || bucket.Height <= 0 ||
            bucket.Width % SpatialMultiple != 0 || bucket.Height % SpatialMultiple != 0)
            throw new ArgumentException($"Bucket dimensions must be positive multiples of {SpatialMultiple}.",
                nameof(bucket));
    }

    public void ValidateFrameRate(double frameRate)
    {
        // FrameRates are automatic recommendations, not a closed training limit.
        // A particular trainer or LoRA recipe may specify another positive rate.
        if (!double.IsFinite(frameRate) || frameRate < 1 || frameRate > 120)
            throw new ArgumentException("Choose an output frame rate from 1 to 120 fps.", nameof(frameRate));
    }

    public override string ToString() => Name;
}
