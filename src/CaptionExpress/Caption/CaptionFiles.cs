namespace CaptionExpress.Caption;

public static class CaptionFiles
{
    public static async Task SaveAsync(string exportedMediaPath, string caption,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(caption))
            throw new ArgumentException("Enter a caption before saving.", nameof(caption));

        string textPath = Path.ChangeExtension(exportedMediaPath, ".txt");
        string partial = textPath + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(partial, caption.Trim() + Environment.NewLine, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, textPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }
}
