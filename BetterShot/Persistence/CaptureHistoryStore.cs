using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterShot.Editing;

namespace BetterShot.Persistence;

internal sealed class CaptureHistoryStore
{
    private const int CurrentVersion = 1;
    private const string BackgroundMaskEntry = "background-mask.png";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _root;
    private readonly string _settingsPath;
    private readonly List<string> _lastLoadErrors = [];
    private readonly HashSet<Guid> _expiredDuringLastPurge = [];
    private CaptureSettings _settings = CaptureSettings.Default;
    private Dictionary<string, JsonElement>? _additionalSettingsFields;
    private static readonly string[] PriorDataFiles = ["ocr-index.json", "recordings.json", "ask-claude-key.bin"];
    private static readonly string[] PriorDataFolders = ["RecordingsInProgress", "MarkedCopies"];
    // The installed app keeps these beside the History folder (%LOCALAPPDATA%\BetterShot).
    private static readonly string[] PriorAppDataFiles = ["ask-claude-key.bin", "ocr-index.json", "editor-styles.json"];

    internal CaptureHistoryStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _settingsPath = Path.Combine(_root, "settings.json");
        Directory.CreateDirectory(_root);
        LoadSettings();
    }

    internal static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BetterShot", "History");

    internal CaptureSettings Settings => _settings;
    /// <summary>Days captures are kept; <see cref="CaptureSettings.NeverExpire"/> (0) keeps them until deleted.</summary>
    internal int RetentionDays => _settings.RetentionDays;
    internal bool KeepForever => _settings.KeepForever;
    internal bool ReplaceScreenshotShortcut => _settings.ReplaceScreenshotShortcut;
    internal bool StartWithWindows => _settings.StartWithWindows;
    internal bool DragAsFile => _settings.DragAsFile;
    internal IReadOnlyList<string> LastLoadErrors => _lastLoadErrors;

    /// <summary>Accepts 1-365 days, or <see cref="CaptureSettings.NeverExpire"/> to keep captures until deleted.</summary>
    internal void SetRetentionDays(int days)
    {
        if (days != CaptureSettings.NeverExpire && days is < 1 or > 365)
            throw new ArgumentOutOfRangeException(nameof(days), "Retention must be between 1 and 365 days, or never.");
        UpdateSettings(settings => settings with { RetentionDays = days });
    }

    internal void SetReplaceScreenshotShortcut(bool enabled) =>
        UpdateSettings(settings => settings with { ReplaceScreenshotShortcut = enabled });

    internal void SetStartWithWindows(bool enabled) =>
        UpdateSettings(settings => settings with { StartWithWindows = enabled });

    internal void SetDragAsFile(bool enabled) =>
        UpdateSettings(settings => settings with { DragAsFile = enabled });

    /// <summary>
    /// Writes the changed snapshot atomically (including every other field and unknown fields from
    /// newer builds) and only then updates the in-memory value, so a failed write changes nothing.
    /// </summary>
    internal void UpdateSettings(Func<CaptureSettings, CaptureSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        CaptureSettings updated = change(_settings).Normalize();
        Directory.CreateDirectory(_root);
        WriteJsonAtomically(_settingsPath, CaptureSettingsMapper.ToDto(updated, _additionalSettingsFields));
        _settings = updated;
    }

    internal IReadOnlyList<CaptureDocument> Load(DateTime utcNow)
    {
        _lastLoadErrors.Clear();
        PurgeExpired(utcNow);

        var documents = new List<CaptureDocument>();
        if (!Directory.Exists(_root)) return documents;

        foreach (string path in Directory.EnumerateFiles(_root, "*.capture", SearchOption.TopDirectoryOnly))
        {
            if (!TryGetOwnedId(path, out Guid fileId) || _expiredDuringLastPurge.Contains(fileId)) continue;
            try
            {
                documents.Add(ReadCapture(path, fileId));
            }
            catch (Exception ex)
            {
                _lastLoadErrors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return documents.OrderByDescending(document => document.CreatedUtc).ToArray();
    }

    internal void Save(CaptureDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Directory.CreateDirectory(_root);

        string destination = CapturePath(document.Id);
        string temporary = Path.Combine(_root, $".{document.Id:N}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                ZipArchiveEntry imageEntry = archive.CreateEntry("original.png", CompressionLevel.Optimal);
                using (var imageBuffer = new MemoryStream())
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(document.Original));
                    encoder.Save(imageBuffer);
                    imageBuffer.Position = 0;
                    using Stream imageStream = imageEntry.Open();
                    imageBuffer.CopyTo(imageStream);
                }

                if (document.BackgroundMask is { } mask)
                {
                    // Optional since phase 5; older builds ignore it and show the opaque original.
                    ZipArchiveEntry maskEntry = archive.CreateEntry(BackgroundMaskEntry, CompressionLevel.Optimal);
                    using Stream maskStream = maskEntry.Open();
                    maskStream.Write(mask.EncodePng());
                }

                ZipArchiveEntry documentEntry = archive.CreateEntry("document.json", CompressionLevel.Optimal);
                using Stream jsonStream = documentEntry.Open();
                JsonSerializer.Serialize(jsonStream, ToDto(document), JsonOptions);
            }

            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal void Delete(Guid id)
    {
        string path = CapturePath(id);
        if (File.Exists(path)) File.Delete(path);
    }

    internal IReadOnlyList<Guid> PurgeExpired(DateTime utcNow)
    {
        _lastLoadErrors.Clear();
        var removed = new List<Guid>();
        _expiredDuringLastPurge.Clear();
        if (_settings.KeepForever || !Directory.Exists(_root)) return removed;
        DateTime cutoff = utcNow.ToUniversalTime().AddDays(-_settings.RetentionDays);

        foreach (string path in Directory.EnumerateFiles(_root, "*.capture", SearchOption.TopDirectoryOnly))
        {
            if (!TryGetOwnedId(path, out Guid fileId)) continue;

            bool expired;
            try
            {
                CapturePackageDto metadata = ReadMetadata(path);
                if (metadata.Id != fileId)
                    throw new InvalidDataException("The package identity does not match its file name.");
                ValidateCreatedUtc(metadata.CreatedUtc);
                expired = metadata.CreatedUtc.ToUniversalTime() <= cutoff;
            }
            catch (Exception metadataError)
            {
                try
                {
                    expired = File.GetLastWriteTimeUtc(path) <= cutoff;
                }
                catch (Exception timestampError)
                {
                    _lastLoadErrors.Add(
                        $"{Path.GetFileName(path)}: retention check failed ({metadataError.Message}; {timestampError.Message})");
                    continue;
                }
            }

            if (!expired) continue;
            _expiredDuringLastPurge.Add(fileId);
            try
            {
                File.Delete(path);
                removed.Add(fileId);
            }
            catch (Exception ex)
            {
                _lastLoadErrors.Add($"{Path.GetFileName(path)}: expired capture could not be deleted ({ex.Message})");
            }
        }

        return removed;
    }
    private CaptureDocument ReadCapture(string path, Guid fileId)
    {
        using var archive = ZipFile.OpenRead(path);
        CapturePackageDto dto = ReadMetadata(archive);
        if (dto.Version != CurrentVersion) throw new InvalidDataException($"Unsupported document version {dto.Version}.");
        if (dto.Id == Guid.Empty || dto.Id != fileId) throw new InvalidDataException("The package identity is invalid.");
        ValidateCreatedUtc(dto.CreatedUtc);

        ZipArchiveEntry imageEntry = archive.GetEntry("original.png")
            ?? throw new InvalidDataException("The original image is missing.");
        BitmapSource original;
        using (var imageBuffer = new MemoryStream())
        {
            using (Stream stream = imageEntry.Open()) stream.CopyTo(imageBuffer);
            imageBuffer.Position = 0;
            var decoder = new PngBitmapDecoder(imageBuffer, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1) throw new InvalidDataException("The original image is invalid.");
            original = decoder.Frames[0];
            if (original.CanFreeze) original.Freeze();
        }

        var annotations = dto.Annotations?.Select(FromDto).ToArray()
            ?? throw new InvalidDataException("The annotation list is missing.");
        var document = new CaptureDocument(original, dto.Id, dto.CreatedUtc);
        document.RestoreState(annotations, FromDto(dto.Crop), FromDto(dto.Background));
        if (archive.GetEntry(BackgroundMaskEntry) is { } maskEntry)
        {
            using var maskBuffer = new MemoryStream();
            using (Stream stream = maskEntry.Open()) stream.CopyTo(maskBuffer);
            maskBuffer.Position = 0;
            var mask = Editing.BackgroundRemoval.AlphaMask.DecodePng(maskBuffer);
            if (mask.Fits(original)) document.RestoreBackgroundMask(mask); // A mismatched mask is ignored rather than losing the capture.
        }
        return document;
    }

    private static CapturePackageDto ReadMetadata(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return ReadMetadata(archive);
    }

    private static CapturePackageDto ReadMetadata(ZipArchive archive)
    {
        ZipArchiveEntry entry = archive.GetEntry("document.json")
            ?? throw new InvalidDataException("Document metadata is missing.");
        using Stream stream = entry.Open();
        return JsonSerializer.Deserialize<CapturePackageDto>(stream, JsonOptions)
            ?? throw new InvalidDataException("Document metadata is empty.");
    }

    private static CapturePackageDto ToDto(CaptureDocument document) => new()
    {
        Version = CurrentVersion,
        Id = document.Id,
        CreatedUtc = document.CreatedUtc,
        Annotations = document.Items.Select(ToDto).ToList(),
        Crop = document.Crop is Rect crop ? new CropDto { X = crop.X, Y = crop.Y, Width = crop.Width, Height = crop.Height } : null,
        Background = document.Background.IsEnabled ? new BackgroundDto
        {
            Preset = document.Background.Preset, Padding = document.Background.Padding,
            CornerRadius = document.Background.CornerRadius, Shadow = document.Background.Shadow
        } : null
    };

    private static Rect? FromDto(CropDto? crop)
    {
        if (crop is null) return null;
        RequireFinite(crop.X, "crop x"); RequireFinite(crop.Y, "crop y");
        RequireFinite(crop.Width, "crop width"); RequireFinite(crop.Height, "crop height");
        if (crop.Width <= 0 || crop.Height <= 0) throw new InvalidDataException("Crop dimensions must be positive.");
        return new Rect(crop.X, crop.Y, crop.Width, crop.Height);
    }

    private static BackgroundStyle? FromDto(BackgroundDto? background)
    {
        if (background is null) return null;
        RequireFinite(background.Padding, "background padding");
        RequireFinite(background.CornerRadius, "background corner radius");
        return new BackgroundStyle(background.Preset ?? BackgroundStyle.NoneId, background.Padding, background.CornerRadius, background.Shadow);
    }

    internal static AnnotationDto ToDto(Annotation annotation)
    {
        var dto = new AnnotationDto
        {
            A = annotation.Color.A, R = annotation.Color.R, G = annotation.Color.G,
            B = annotation.Color.B, Width = annotation.Width,
            Opacity = annotation.Opacity < 1 ? annotation.Opacity : null,
            Dashed = annotation.Dashed ? true : null,
            Shadow = annotation.Shadow ? true : null,
            Fill = annotation.Fill is Color fill ? new ColorDto { A = fill.A, R = fill.R, G = fill.G, B = fill.B } : null
        };
        switch (annotation)
        {
            case ArrowAnnotation arrow:
                dto.Kind = "arrow";
                SetPair(dto, arrow.Start, arrow.End);
                break;
            case RectangleAnnotation rectangle:
                dto.Kind = "rectangle";
                dto.X1 = rectangle.Bounds.X; dto.Y1 = rectangle.Bounds.Y;
                dto.X2 = rectangle.Bounds.Width; dto.Y2 = rectangle.Bounds.Height;
                dto.Redaction = rectangle.Redaction;
                dto.CornerRadius = rectangle.CornerRadius;
                dto.RedactionStyle = rectangle.Redaction && rectangle.RedactionMode != RedactionStyle.Solid
                    ? rectangle.RedactionMode.ToString().ToLowerInvariant() : null;
                break;
            case EllipseAnnotation ellipse:
                dto.Kind = "ellipse";
                SetBox(dto, ellipse.Bounds);
                break;
            case LineAnnotation line:
                dto.Kind = "line";
                SetPair(dto, line.Start, line.End);
                break;
            case HighlighterAnnotation highlighter:
                dto.Kind = "highlighter";
                dto.Points = highlighter.Points.Select(p => new PointDto { X = p.X, Y = p.Y }).ToList();
                break;
            case SpotlightAnnotation spotlight:
                dto.Kind = "spotlight";
                SetBox(dto, spotlight.Bounds);
                dto.CornerRadius = spotlight.CornerRadius;
                break;
            case MagnifyAnnotation magnify:
                dto.Kind = "magnify";
                dto.X1 = magnify.Center.X; dto.Y1 = magnify.Center.Y;
                dto.Radius = magnify.Radius; dto.Zoom = magnify.Zoom;
                break;
            case PenAnnotation pen:
                dto.Kind = "pen";
                dto.Points = pen.Points.Select(p => new PointDto { X = p.X, Y = p.Y }).ToList();
                break;
            case TextAnnotation text:
                dto.Kind = text.Tail is null ? "text" : "callout";
                dto.X1 = text.Position.X; dto.Y1 = text.Position.Y;
                dto.Text = text.Text; dto.Radius = text.FontSize;
                dto.Tail = text.Tail is Point tail ? new PointDto { X = tail.X, Y = tail.Y } : null;
                dto.TextBackground = text.Background ? true : null;
                dto.WrapWidth = text.WrapWidth;
                break;
            case StepAnnotation step:
                dto.Kind = "step";
                dto.X1 = step.Center.X; dto.Y1 = step.Center.Y;
                dto.Number = step.Number; dto.Radius = step.Radius;
                break;
            default:
                throw new InvalidDataException($"Unsupported annotation type {annotation.GetType().Name}.");
        }
        return dto;
    }

    internal static Annotation FromDto(AnnotationDto dto)
    {
        if (dto is null) throw new InvalidDataException("An annotation is null.");
        Color color = Color.FromArgb(dto.A, dto.R, dto.G, dto.B);
        RequireFinite(dto.Width, nameof(dto.Width));
        if (dto.Width < 0) throw new InvalidDataException("Annotation width cannot be negative.");

        Annotation annotation = dto.Kind switch
        {
            "arrow" => new ArrowAnnotation(Point(dto.X1, dto.Y1), Point(dto.X2, dto.Y2), color, dto.Width),
            "rectangle" => Rectangle(dto, color),
            "pen" => new PenAnnotation(Points(dto, "Pen"), color, dto.Width),
            "text" => Text(dto, color),
            "callout" => Callout(dto, color),
            "step" => Step(dto, color),
            "ellipse" => new EllipseAnnotation(Box(dto, "ellipse"), color, dto.Width),
            "line" => new LineAnnotation(Point(dto.X1, dto.Y1), Point(dto.X2, dto.Y2), color, dto.Width),
            "highlighter" => new HighlighterAnnotation(Points(dto, "Highlighter"), color, dto.Width),
            "spotlight" => new SpotlightAnnotation(Box(dto, "spotlight"), NonNegative(dto.CornerRadius ?? 0, "spotlight corner radius")),
            "magnify" => Magnify(dto, color),
            _ => throw new InvalidDataException($"Unknown annotation kind '{dto.Kind}'.")
        };
        return WithStoredStyle(annotation, dto);
    }

    private static Annotation WithStoredStyle(Annotation annotation, AnnotationDto dto)
    {
        double opacity = dto.Opacity ?? 1;
        RequireFinite(opacity, "opacity");
        return annotation with
        {
            Opacity = Math.Clamp(opacity, .1, 1),
            Dashed = dto.Dashed ?? false,
            Shadow = dto.Shadow ?? false,
            Fill = dto.Fill is ColorDto fill ? Color.FromArgb(fill.A, fill.R, fill.G, fill.B) : null
        };
    }

    private static Point[] Points(AnnotationDto dto, string kind) =>
        (dto.Points ?? throw new InvalidDataException($"{kind} points are missing.")).Select(p => Point(p.X, p.Y)).ToArray();

    private static Rect Box(AnnotationDto dto, string kind)
    {
        RequireFinite(dto.X2, $"{kind} width"); RequireFinite(dto.Y2, $"{kind} height");
        if (dto.X2 < 0 || dto.Y2 < 0) throw new InvalidDataException($"{kind} dimensions cannot be negative.");
        return new Rect(Point(dto.X1, dto.Y1), new Size(dto.X2, dto.Y2));
    }

    private static double NonNegative(double value, string name)
    {
        RequireFinite(value, name);
        if (value < 0) throw new InvalidDataException($"{name} cannot be negative.");
        return value;
    }

    private static MagnifyAnnotation Magnify(AnnotationDto dto, Color color)
    {
        RequireFinite(dto.Radius, "magnifier radius");
        if (dto.Radius <= 0) throw new InvalidDataException("Magnifier radius must be positive.");
        double zoom = dto.Zoom ?? 2;
        RequireFinite(zoom, "magnifier zoom");
        return new MagnifyAnnotation(Point(dto.X1, dto.Y1), dto.Radius, Math.Clamp(zoom, 1, 8), color, dto.Width);
    }

    private static RectangleAnnotation Rectangle(AnnotationDto dto, Color color)
    {
        RequireFinite(dto.X2, "rectangle width"); RequireFinite(dto.Y2, "rectangle height");
        if (dto.X2 < 0 || dto.Y2 < 0) throw new InvalidDataException("Rectangle dimensions cannot be negative.");
        // Unknown redaction styles fall back to Solid so a damaged file can never un-hide pixels.
        RedactionStyle mode = dto.RedactionStyle?.ToLowerInvariant() switch
        {
            "pixelate" => RedactionStyle.Pixelate,
            "blur" => RedactionStyle.Blur,
            _ => RedactionStyle.Solid
        };
        return new RectangleAnnotation(new Rect(Point(dto.X1, dto.Y1), new Size(dto.X2, dto.Y2)),
            color, dto.Width, dto.Redaction)
        {
            CornerRadius = dto.CornerRadius is double radius ? NonNegative(radius, "corner radius") : null,
            RedactionMode = mode
        };
    }

    private static TextAnnotation Text(AnnotationDto dto, Color color)
    {
        if (dto.Text is null) throw new InvalidDataException("Text content is missing.");
        RequireFinite(dto.Radius, "font size");
        if (dto.Radius <= 0) throw new InvalidDataException("Font size must be positive.");
        return new TextAnnotation(Point(dto.X1, dto.Y1), dto.Text, color, dto.Radius) { Background = dto.TextBackground ?? false };
    }

    private static TextAnnotation Callout(AnnotationDto dto, Color color)
    {
        PointDto tail = dto.Tail ?? throw new InvalidDataException("Callout tail is missing.");
        double? wrapWidth = dto.WrapWidth is double width && double.IsFinite(width) && width > 0 ? width : null;
        return Text(dto, color) with { Tail = Point(tail.X, tail.Y), Background = false, WrapWidth = wrapWidth };
    }

    private static StepAnnotation Step(AnnotationDto dto, Color color)
    {
        RequireFinite(dto.Radius, "step radius");
        if (dto.Radius < 0) throw new InvalidDataException("Step radius cannot be negative.");
        return new StepAnnotation(Point(dto.X1, dto.Y1), dto.Number, color, dto.Radius);
    }

    private static Point Point(double x, double y)
    {
        RequireFinite(x, "point x"); RequireFinite(y, "point y");
        return new Point(x, y);
    }

    private static void SetBox(AnnotationDto dto, Rect box)
    {
        dto.X1 = box.X; dto.Y1 = box.Y; dto.X2 = box.Width; dto.Y2 = box.Height;
    }

    private static void SetPair(AnnotationDto dto, Point first, Point second)
    {
        dto.X1 = first.X; dto.Y1 = first.Y; dto.X2 = second.X; dto.Y2 = second.Y;
    }

    private static void RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value)) throw new InvalidDataException($"{name} is not finite.");
    }

    private static void ValidateCreatedUtc(DateTime createdUtc)
    {
        if (createdUtc == default) throw new InvalidDataException("Creation time is missing.");
    }

    private string CapturePath(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("Capture identity cannot be empty.", nameof(id));
        return Path.Combine(_root, $"{id:N}.capture");
    }

    private static bool TryGetOwnedId(string path, out Guid id)
    {
        id = Guid.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        return name.Length == 32 && Guid.TryParseExact(name, "N", out id);
    }

    private void LoadSettings()
    {
        if (!TryLoadSavedSettings())
            _settings = CaptureSettings.Default with { OnboardingCompleted = HasPriorBetterShotData() };
    }

    /// <summary>Returns false when the safe defaults must be used (no file, unreadable, corrupt, or invalid retention).</summary>
    private bool TryLoadSavedSettings()
    {
        if (!File.Exists(_settingsPath)) return false;
        try
        {
            HistorySettingsDto? settings = JsonSerializer.Deserialize<HistorySettingsDto>(
                File.ReadAllText(_settingsPath), JsonOptions);
            if (settings?.RetentionDays is >= 1 and <= 365)
            {
                _settings = CaptureSettingsMapper.FromDto(settings);
                _additionalSettingsFields = settings.AdditionalFields;
                return true;
            }
        }
        catch (JsonException)
        {
            // Keep the safe default when settings were interrupted or manually corrupted.
        }
        catch (IOException)
        {
            // Keep the safe default if settings cannot currently be read.
        }
        return false;
    }

    /// <summary>
    /// Earlier builds only wrote settings.json after a setting changed, so a missing or unusable settings file does not
    /// mean a new install. Any data an earlier run left behind (the constructor creates the folder itself, so the folder
    /// alone proves nothing) marks the user as existing so first-run setup is not shown again.
    /// </summary>
    private bool HasPriorBetterShotData()
    {
        try
        {
            if (File.Exists(_settingsPath)) return true;
            if (Directory.EnumerateFiles(_root, "*.capture", SearchOption.TopDirectoryOnly).Any()) return true;
            if (PriorDataFiles.Any(name => File.Exists(Path.Combine(_root, name)))) return true;
            if (PriorDataFolders.Any(name => Directory.Exists(Path.Combine(_root, name)))) return true;
            string? appDataFolder = Path.GetDirectoryName(_root);
            return appDataFolder is not null
                && PriorAppDataFiles.Any(name => File.Exists(Path.Combine(appDataFolder, name)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WriteJsonAtomically<T>(string destination, T value)
    {
        string directory = Path.GetDirectoryName(destination)!;
        string temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}