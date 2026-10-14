using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using ClickyWindows.Helpers;
using ClickyWindows.Settings;

namespace ClickyWindows.Services;

/// <param name="Base64">JPEG for the Claude API</param>
/// <param name="Label">Text sent next to the image: screen number, image size, cursor position</param>
/// <param name="Bounds">Physical pixel bounds of the captured screen</param>
/// <param name="Scale">Image pixels per physical pixel (≤ 1)</param>
/// <param name="Number">1 = the screen with the cursor</param>
public record ScreenshotResult(string Base64, string Label, Rectangle Bounds, int Width, int Height, double Scale, int Number)
{
    /// <summary>Maps a point in this image's pixel space back to physical screen pixels.</summary>
    public (double x, double y) ToPhysical(int imageX, int imageY) =>
        (Bounds.Left + Math.Clamp(imageX, 0, Width - 1) / Scale,
         Bounds.Top + Math.Clamp(imageY, 0, Height - 1) / Scale);
}

/// <summary>
/// Captures every monitor with GDI and downsizes each image to stay inside Claude's
/// no-resize limits (long edge and megapixels). If Claude resized the image itself, the
/// [POINT:x,y] coordinates it returns would be in an unknown scale; this way they map back
/// to the screen exactly. The screen under the cursor is always screen 1.
///
/// One frame per question, so GDI is fast enough (tens of ms); DXGI duplication would only
/// pay off for continuous capture. Call from a background thread.
/// </summary>
public sealed class ScreenCaptureService(ScreenshotConfig config)
{
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public List<ScreenshotResult> CaptureAll()
    {
        Win32.GetCursorPos(out var cursor);
        var screens = Screen.AllScreens
            .OrderByDescending(s => s.Bounds.Contains(cursor.X, cursor.Y))
            .ToList();

        var results = new List<ScreenshotResult>(screens.Count);
        for (int i = 0; i < screens.Count; i++)
        {
            try
            {
                results.Add(Capture(screens[i].Bounds, i + 1, screens.Count, cursor));
            }
            catch (Exception ex)
            {
                Logger.Log($"[Screen] Capture of screen {i + 1} failed: {ex.Message}");
            }
        }
        return results;
    }

    private ScreenshotResult Capture(Rectangle bounds, int number, int total, Win32.POINT cursor)
    {
        double scale = Math.Min(1.0, Math.Min(
            (double)config.MaxLongEdge / Math.Max(bounds.Width, bounds.Height),
            Math.Sqrt(config.MaxMegapixels * 1_000_000 / ((double)bounds.Width * bounds.Height))));
        int width = Math.Max(1, (int)Math.Floor(bounds.Width * scale));
        int height = Math.Max(1, (int)Math.Floor(bounds.Height * scale));
        scale = (double)width / bounds.Width; // exact ratio after rounding

        using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);

        string base64;
        if (width == bounds.Width && height == bounds.Height)
        {
            base64 = EncodeJpeg(full);
        }
        else
        {
            using var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(full, 0, 0, width, height);
            }
            base64 = EncodeJpeg(scaled);
        }

        string label = $"Screen {number} of {total} — image is {width}x{height} pixels.";
        if (bounds.Contains(cursor.X, cursor.Y))
        {
            int cx = (int)((cursor.X - bounds.Left) * scale);
            int cy = (int)((cursor.Y - bounds.Top) * scale);
            label += $" The user's mouse cursor is at ({cx},{cy}) in this image.";
        }

        return new ScreenshotResult(base64, label, bounds, width, height, scale, number);
    }

    /// <summary>Full-resolution crop of a physical screen region (precise pointing); Scale 1 so coordinates map 1:1.</summary>
    public ScreenshotResult CaptureRegion(Rectangle region)
    {
        using var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap))
            g.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size);
        return new ScreenshotResult(EncodeJpeg(bitmap),
            $"Full-resolution crop of the screen — image is {region.Width}x{region.Height} pixels.",
            region, region.Width, region.Height, 1.0, 1);
    }

    // ── Change detection (tutor mode) ───────────────────────────────────────

    private const int ThumbWidth = 96, ThumbHeight = 54;

    /// <summary>A 96×54 grayscale thumbnail of the cursor's screen — cheap enough to take every second.</summary>
    public static byte[] Thumbnail()
    {
        Win32.GetCursorPos(out var cursor);
        var bounds = Screen.FromPoint(new Point(cursor.X, cursor.Y)).Bounds;

        using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        using var small = new Bitmap(ThumbWidth, ThumbHeight, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(full, 0, 0, ThumbWidth, ThumbHeight);
        }

        var gray = new byte[ThumbWidth * ThumbHeight];
        var data = small.LockBits(new Rectangle(0, 0, ThumbWidth, ThumbHeight), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[data.Stride];
            for (int y = 0; y < ThumbHeight; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (int x = 0; x < ThumbWidth; x++)
                    gray[y * ThumbWidth + x] = (byte)((row[3 * x] + 2 * row[3 * x + 1] + row[3 * x + 2]) / 4);
            }
        }
        finally
        {
            small.UnlockBits(data);
        }
        return gray;
    }

    /// <summary>True when more than ~1% of the thumbnail visibly changed (a caret blink or clock tick doesn't count).</summary>
    public static bool Differs(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return true;
        int changed = 0;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 24) changed++;
        return changed > a.Length / 100;
    }

    private string EncodeJpeg(Bitmap bitmap)
    {
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)config.JpegQuality);
        using var ms = new MemoryStream();
        bitmap.Save(ms, JpegCodec, parameters);
        return Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
    }
}
