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
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _root;
    private readonly string _settingsPath;
    private readonly List<string> _lastLoadErrors = [];
    private readonly HashSet<Guid> _expiredDuringLastPurge = [];
    private int _retentionDays = 7;
    private bool _replaceScreenshotShortcut;
    private bool _startWithWindows;
    private bool _dragAsFile;

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

    internal int RetentionDays => _retentionDays;
    internal bool ReplaceScreenshotShortcut => _replaceScreenshotShortcut;
    internal bool StartWithWindows => _startWithWindows;
    internal bool DragAsFile => _dragAsFile;
    internal IReadOnlyList<string> LastLoadErrors => _lastLoadErrors;

    internal void SetRetentionDays(int days)
    {
        if (days is < 1 or > 365)
            throw new ArgumentOutOfRangeException(nameof(days), "Retention must be between 1 and 365 days.");

        Directory.CreateDirectory(_root);
        WriteSettings(days, _replaceScreenshotShortcut, _startWithWindows, _dragAsFile);
        _retentionDays = days;
    }

    internal void SetReplaceScreenshotShortcut(bool enabled)
    {
        Directory.CreateDirectory(_root);
        WriteSettings(_retentionDays, enabled, _startWithWindows, _dragAsFile);
        _replaceScreenshotShortcut = enabled;
    }

    internal void SetStartWithWindows(bool enabled)
    {
        Directory.CreateDirectory(_root);
        WriteSettings(_retentionDays, _replaceScreenshotShortcut, enabled, _dragAsFile);
        _startWithWindows = enabled;
    }

    internal void SetDragAsFile(bool enabled)
    {
        Directory.CreateDirectory(_root);
        WriteSettings(_retentionDays, _replaceScreenshotShortcut, _startWithWindows, enabled);
        _dragAsFile = enabled;
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
        DateTime cutoff = utcNow.ToUniversalTime().AddDays(-_retentionDays);
        var removed = new List<Guid>();
        _expiredDuringLastPurge.Clear();
        if (!Directory.Exists(_root)) return removed;

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
        document.RestoreAnnotations(annotations);
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
        Annotations = document.Items.Select(ToDto).ToList()
    };

    private static AnnotationDto ToDto(Annotation annotation)
    {
        var dto = new AnnotationDto
        {
            A = annotation.Color.A, R = annotation.Color.R, G = annotation.Color.G,
            B = annotation.Color.B, Width = annotation.Width
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
                break;
            case PenAnnotation pen:
                dto.Kind = "pen";
                dto.Points = pen.Points.Select(p => new PointDto { X = p.X, Y = p.Y }).ToList();
                break;
            case TextAnnotation text:
                dto.Kind = "text";
                dto.X1 = text.Position.X; dto.Y1 = text.Position.Y;
                dto.Text = text.Text; dto.Radius = text.FontSize;
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

    private static Annotation FromDto(AnnotationDto dto)
    {
        if (dto is null) throw new InvalidDataException("An annotation is null.");
        Color color = Color.FromArgb(dto.A, dto.R, dto.G, dto.B);
        RequireFinite(dto.Width, nameof(dto.Width));
        if (dto.Width < 0) throw new InvalidDataException("Annotation width cannot be negative.");

        return dto.Kind switch
        {
            "arrow" => new ArrowAnnotation(Point(dto.X1, dto.Y1), Point(dto.X2, dto.Y2), color, dto.Width),
            "rectangle" => Rectangle(dto, color),
            "pen" => new PenAnnotation((dto.Points ?? throw new InvalidDataException("Pen points are missing."))
                .Select(p => Point(p.X, p.Y)).ToArray(), color, dto.Width),
            "text" => Text(dto, color),
            "step" => Step(dto, color),
            _ => throw new InvalidDataException($"Unknown annotation kind '{dto.Kind}'.")
        };
    }

    private static RectangleAnnotation Rectangle(AnnotationDto dto, Color color)
    {
        RequireFinite(dto.X2, "rectangle width"); RequireFinite(dto.Y2, "rectangle height");
        if (dto.X2 < 0 || dto.Y2 < 0) throw new InvalidDataException("Rectangle dimensions cannot be negative.");
        return new RectangleAnnotation(new Rect(Point(dto.X1, dto.Y1), new Size(dto.X2, dto.Y2)),
            color, dto.Width, dto.Redaction);
    }

    private static TextAnnotation Text(AnnotationDto dto, Color color)
    {
        if (dto.Text is null) throw new InvalidDataException("Text content is missing.");
        RequireFinite(dto.Radius, "font size");
        if (dto.Radius <= 0) throw new InvalidDataException("Font size must be positive.");
        return new TextAnnotation(Point(dto.X1, dto.Y1), dto.Text, color, dto.Radius);
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
        if (!File.Exists(_settingsPath)) return;
        try
        {
            HistorySettingsDto? settings = JsonSerializer.Deserialize<HistorySettingsDto>(
                File.ReadAllText(_settingsPath), JsonOptions);
            if (settings?.RetentionDays is >= 1 and <= 365)
            {
                _retentionDays = settings.RetentionDays;
                _replaceScreenshotShortcut = settings.ReplaceScreenshotShortcut;
                _startWithWindows = settings.StartWithWindows;
                _dragAsFile = settings.DragAsFile;
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
    }

    private void WriteSettings(int retentionDays, bool replaceScreenshotShortcut, bool startWithWindows, bool dragAsFile) =>
        WriteJsonAtomically(_settingsPath, new HistorySettingsDto
        {
            RetentionDays = retentionDays,
            ReplaceScreenshotShortcut = replaceScreenshotShortcut,
            StartWithWindows = startWithWindows,
            DragAsFile = dragAsFile
        });

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