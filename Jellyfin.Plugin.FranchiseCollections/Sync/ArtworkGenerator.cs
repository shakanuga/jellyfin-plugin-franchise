using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SkiaSharp;

namespace Jellyfin.Plugin.FranchiseCollections.Sync;

/// <summary>
/// Erzeugt Poster (2:3) und Vorschaubilder (16:9) für Sammlungen und Playlists:
/// Hintergrundbild eines Schlüsseltitels, abgedunkelt, darauf Kicker / großer Titel / Akzentstrich / Untertitel.
/// Schrift: Inter Display (SIL Open Font License, liegt im Plugin).
/// </summary>
public static class ArtworkGenerator
{
    /// <summary>Wird erhöht, wenn sich das Aussehen ändert → alle Bilder werden neu erzeugt.</summary>
    public const int Version = 1;

    private static readonly Lazy<SKTypeface> Black = new(() => LoadFont("InterDisplay-Black.otf"));
    private static readonly Lazy<SKTypeface> SemiBold = new(() => LoadFont("InterDisplay-SemiBold.otf"));

    public sealed record Texts(string? Kicker, string Title, string? Subtitle);

    public static byte[] CreatePoster(string backdropPath, Texts texts)
    {
        const int W = 1000, H = 1500;
        using var source = SKBitmap.Decode(backdropPath) ?? throw new InvalidDataException("Bild nicht lesbar: " + backdropPath);
        var accent = AccentColor(source);

        var info = new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        DrawCover(canvas, source, new SKRect(0, 0, W, H), focusX: 0.5f);

        // abdunkeln: leicht überall, nach unten stark
        using (var shade = new SKPaint())
        {
            shade.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, H),
                new[] { new SKColor(0, 0, 0, 40), new SKColor(0, 0, 0, 40), new SKColor(0, 0, 0, 170), new SKColor(0, 0, 0, 245) },
                new[] { 0f, 0.36f, 0.68f, 1f },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(new SKRect(0, 0, W, H), shade);
        }

        DrawTextBlock(canvas, texts, accent, left: 70, bottom: H - 110, maxWidth: 860, titleMax: 170, titleMin: 64, maxLines: 3, kickerSize: 34, subtitleSize: 40);
        return Encode(surface);
    }

    public static byte[] CreateThumb(string backdropPath, Texts texts)
    {
        const int W = 1920, H = 1080;
        using var source = SKBitmap.Decode(backdropPath) ?? throw new InvalidDataException("Bild nicht lesbar: " + backdropPath);
        var accent = AccentColor(source);

        var info = new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        DrawCover(canvas, source, new SKRect(0, 0, W, H), focusX: 0.65f);

        using (var shade = new SKPaint())
        {
            shade.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(W, 0),
                new[] { new SKColor(0, 0, 0, 235), new SKColor(0, 0, 0, 170), new SKColor(0, 0, 0, 20) },
                new[] { 0f, 0.42f, 0.78f },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(new SKRect(0, 0, W, H), shade);
        }

        using (var bottom = new SKPaint())
        {
            bottom.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, H * 0.55f),
                new SKPoint(0, H),
                new[] { SKColors.Transparent, new SKColor(0, 0, 0, 160) },
                null,
                SKShaderTileMode.Clamp);
            canvas.DrawRect(new SKRect(0, 0, W, H), bottom);
        }

        DrawTextBlock(canvas, texts, accent, left: 110, bottom: H - 130, maxWidth: 1050, titleMax: 160, titleMin: 70, maxLines: 2, kickerSize: 38, subtitleSize: 44);
        return Encode(surface);
    }

    private static void DrawTextBlock(
        SKCanvas canvas,
        Texts texts,
        SKColor accent,
        float left,
        float bottom,
        float maxWidth,
        float titleMax,
        float titleMin,
        int maxLines,
        float kickerSize,
        float subtitleSize)
    {
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var soft = new SKPaint { Color = new SKColor(255, 255, 255, 215), IsAntialias = true };
        using var dim = new SKPaint { Color = new SKColor(255, 255, 255, 190), IsAntialias = true };
        using var accentPaint = new SKPaint { Color = accent, IsAntialias = true };

        var y = bottom;
        if (!string.IsNullOrWhiteSpace(texts.Subtitle))
        {
            using var subFont = new SKFont(SemiBold.Value, subtitleSize);
            canvas.DrawText(Ellipsize(texts.Subtitle!, subFont, maxWidth), left, y, SKTextAlign.Left, subFont, soft);
            y -= subtitleSize + 28;
        }

        canvas.DrawRoundRect(new SKRect(left, y - 8, left + 90, y), 4, 4, accentPaint);
        y -= 34;

        var (titleFont, lines) = FitTitle(texts.Title.ToUpperInvariant(), maxWidth, titleMax, titleMin, maxLines);
        using (titleFont)
        {
            var lineHeight = titleFont.Size * 0.98f;
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                canvas.DrawText(lines[i], left - (titleFont.Size * 0.025f), y, SKTextAlign.Left, titleFont, white);
                y -= lineHeight;
            }

            if (!string.IsNullOrWhiteSpace(texts.Kicker))
            {
                using var kickerFont = new SKFont(SemiBold.Value, kickerSize);
                DrawTracked(canvas, texts.Kicker!.ToUpperInvariant(), left, y + (lineHeight * 0.08f) - 6, kickerFont, dim, kickerSize * 0.12f, maxWidth);
            }
        }
    }

    /// <summary>Größte Schriftgröße, bei der der Titel in höchstens maxLines Zeilen passt.</summary>
    private static (SKFont Font, List<string> Lines) FitTitle(string title, float maxWidth, float maxSize, float minSize, int maxLines)
    {
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var size = maxSize; size >= minSize; size -= 4)
        {
            var font = new SKFont(Black.Value, size);
            var lines = Wrap(words, font, maxWidth);
            if (lines.Count <= maxLines && lines.All(l => font.MeasureText(l) <= maxWidth))
            {
                return (font, lines);
            }

            font.Dispose();
        }

        var fallback = new SKFont(Black.Value, minSize);
        var wrapped = Wrap(words, fallback, maxWidth).Take(maxLines).Select(l => Ellipsize(l, fallback, maxWidth)).ToList();
        return (fallback, wrapped);
    }

    private static List<string> Wrap(string[] words, SKFont font, float maxWidth)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (font.MeasureText(candidate) <= maxWidth || current.Length == 0)
            {
                current = candidate;
            }
            else
            {
                lines.Add(current);
                current = word;
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        return lines;
    }

    private static string Ellipsize(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth)
        {
            return text;
        }

        while (text.Length > 1 && font.MeasureText(text + "…") > maxWidth)
        {
            text = text[..^1];
        }

        return text.TrimEnd() + "…";
    }

    private static void DrawTracked(SKCanvas canvas, string text, float x, float y, SKFont font, SKPaint paint, float tracking, float maxWidth)
    {
        var start = x;
        foreach (var ch in text)
        {
            var s = ch.ToString();
            var w = font.MeasureText(s);
            if (x - start + w > maxWidth)
            {
                break;
            }

            canvas.DrawText(s, x, y, SKTextAlign.Left, font, paint);
            x += w + tracking;
        }
    }

    /// <summary>Bild so skalieren, dass es den Bereich füllt (Überstand wird abgeschnitten).</summary>
    private static void DrawCover(SKCanvas canvas, SKBitmap source, SKRect target, float focusX)
    {
        var scale = Math.Max(target.Width / source.Width, target.Height / source.Height);
        var w = source.Width * scale;
        var h = source.Height * scale;
        var left = target.Left - ((w - target.Width) * focusX);
        var top = target.Top - ((h - target.Height) * 0.5f);

        using var image = SKImage.FromBitmap(source);
        canvas.Save();
        canvas.ClipRect(target);
        canvas.DrawImage(image, new SKRect(left, top, left + w, top + h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        canvas.Restore();
    }

    /// <summary>Kräftigste Farbe des Bildes, aufgehellt – für den Akzentstrich.</summary>
    private static SKColor AccentColor(SKBitmap bmp)
    {
        var best = new SKColor(255, 170, 60);
        var bestScore = 0f;
        for (var gx = 0; gx < 16; gx++)
        {
            for (var gy = 0; gy < 9; gy++)
            {
                var c = bmp.GetPixel((int)((gx + 0.5f) * bmp.Width / 16), (int)((gy + 0.5f) * bmp.Height / 9));
                c.ToHsv(out var hue, out var sat, out var val);
                var score = sat * val;
                if (score > bestScore && val > 25)
                {
                    bestScore = score;
                    best = SKColor.FromHsv(hue, Math.Max(sat, 65), 100);
                }
            }
        }

        return bestScore < 900 ? new SKColor(255, 170, 60) : best; // graues Bild → Standard-Orange
    }

    private static byte[] Encode(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static SKTypeface LoadFont(string file)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().First(n => n.EndsWith(file, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return SKTypeface.FromData(SKData.CreateCopy(ms.ToArray())) ?? SKTypeface.Default;
    }
}
