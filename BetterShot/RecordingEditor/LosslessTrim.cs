using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;

namespace BetterShot.RecordingEditor;

/// <summary>One ISO base media (MP4) box: a leaf with its payload, or a container with child boxes.</summary>
internal sealed class Mp4Box
{
    internal static readonly HashSet<string> ContainerTypes = ["moov", "trak", "mdia", "minf", "stbl", "edts", "dinf"];

    internal Mp4Box(string type, byte[]? payload, List<Mp4Box>? children)
    {
        Type = type;
        Payload = payload;
        Children = children;
    }

    internal string Type { get; }
    internal byte[]? Payload { get; set; }
    internal List<Mp4Box>? Children { get; }

    internal Mp4Box? Child(string type) => Children?.FirstOrDefault(child => child.Type == type);

    internal Mp4Box Required(string type) => Child(type) ?? throw new NotSupportedException($"The movie has no '{type}' box.");

    internal Mp4Box Clone() => new(Type, Payload?.ToArray(), Children?.Select(child => child.Clone()).ToList());

    internal static List<Mp4Box> ParseChildren(ReadOnlySpan<byte> data)
    {
        var boxes = new List<Mp4Box>();
        int position = 0;
        while (position + 8 <= data.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            string type = Encoding.ASCII.GetString(data.Slice(position + 4, 4));
            int header = 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(position + 8)..]);
                header = 16;
            }
            else if (size == 0) size = data.Length - position;
            if (size < header || position + size > data.Length) throw new InvalidDataException($"The '{type}' box is damaged.");
            ReadOnlySpan<byte> payload = data.Slice(position + header, (int)size - header);
            boxes.Add(ContainerTypes.Contains(type) ? new Mp4Box(type, null, ParseChildren(payload)) : new Mp4Box(type, payload.ToArray(), null));
            position += (int)size;
        }
        return boxes;
    }

    internal byte[] Serialize()
    {
        using var stream = new MemoryStream();
        WriteTo(stream);
        return stream.ToArray();
    }

    internal void WriteTo(Stream stream)
    {
        byte[] body;
        if (Children is not null)
        {
            using var inner = new MemoryStream();
            foreach (Mp4Box child in Children) child.WriteTo(inner);
            body = inner.ToArray();
        }
        else body = Payload ?? [];
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(body.Length + 8));
        Encoding.ASCII.GetBytes(Type, header[4..]);
        stream.Write(header);
        stream.Write(body);
    }
}

/// <summary>The samples of one track of an MP4, with their file positions and presentation times.</summary>
internal sealed class Mp4Track
{
    internal Mp4Track(Mp4Box trak, string handler, uint timescale, long[] offsets, int[] sizes, long[] decodeTimes, uint[] durations, bool[] sync,
        bool hasSyncTable, long mediaTimeOffset, double leadingEmptySeconds)
    {
        Trak = trak;
        Handler = handler;
        Timescale = timescale;
        Offsets = offsets;
        Sizes = sizes;
        DecodeTimes = decodeTimes;
        Durations = durations;
        Sync = sync;
        HasSyncTable = hasSyncTable;
        MediaTimeOffset = mediaTimeOffset;
        LeadingEmptySeconds = leadingEmptySeconds;
    }

    internal Mp4Box Trak { get; }
    internal string Handler { get; }
    internal uint Timescale { get; }
    internal long[] Offsets { get; }
    internal int[] Sizes { get; }
    internal long[] DecodeTimes { get; }
    internal uint[] Durations { get; }
    internal bool[] Sync { get; }
    internal bool HasSyncTable { get; }
    internal long MediaTimeOffset { get; }
    internal double LeadingEmptySeconds { get; }
    internal int Count => Sizes.Length;
    internal bool IsVideo => Handler == "vide";

    /// <summary>Presentation time of a sample in seconds (edit list applied).</summary>
    internal double TimeOf(int sample) => (DecodeTimes[sample] - MediaTimeOffset) / (double)Timescale + LeadingEmptySeconds;

    internal double DurationOf(int sample) => Durations[sample] / (double)Timescale;
}

/// <summary>
/// Reads the structure of an MP4 written by Windows' encoder (one video track, at most one audio track, no B-frames,
/// not fragmented) well enough to copy samples without decoding. Anything else is reported as
/// <see cref="NotSupportedException"/>, and the editor falls back to a precise re-encoded trim.
/// </summary>
internal sealed class Mp4MovieIndex
{
    private Mp4MovieIndex(byte[]? fileType, Mp4Box movie, uint movieTimescale, Mp4Track video, Mp4Track? audio)
    {
        FileType = fileType;
        Movie = movie;
        MovieTimescale = movieTimescale;
        Video = video;
        Audio = audio;
        KeyframeTimes = Enumerable.Range(0, video.Count).Where(sample => video.Sync[sample]).Select(video.TimeOf).ToArray();
    }

    internal byte[]? FileType { get; }
    internal Mp4Box Movie { get; }
    internal uint MovieTimescale { get; }
    internal Mp4Track Video { get; }
    internal Mp4Track? Audio { get; }
    internal IReadOnlyList<double> KeyframeTimes { get; }

    internal static Mp4MovieIndex Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Read(stream);
    }

    internal static Mp4MovieIndex Read(Stream stream)
    {
        byte[]? fileType = null;
        Mp4Box? movie = null;
        Span<byte> header = stackalloc byte[16];
        long position = 0;
        while (position + 8 <= stream.Length)
        {
            stream.Position = position;
            stream.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = Encoding.ASCII.GetString(header.Slice(4, 4));
            int headerSize = 8;
            if (size == 1)
            {
                stream.ReadExactly(header.Slice(8, 8));
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerSize = 16;
            }
            else if (size == 0) size = stream.Length - position;
            if (size < headerSize || position + size > stream.Length) throw new InvalidDataException($"The '{type}' box is damaged.");
            if (type is "moof" or "mvex") throw new NotSupportedException("Fragmented movies are trimmed by re-encoding.");
            if (type is "ftyp" or "moov")
            {
                if (size > 64 * 1024 * 1024) throw new NotSupportedException("The movie's index is unusually large.");
                byte[] payload = new byte[size - headerSize];
                stream.ReadExactly(payload);
                if (type == "ftyp") fileType = payload;
                else movie = new Mp4Box("moov", null, Mp4Box.ParseChildren(payload));
            }
            position += size;
        }
        if (movie is null) throw new InvalidDataException("The movie has no index ('moov' box).");
        return FromMovie(fileType, movie);
    }

    private static Mp4MovieIndex FromMovie(byte[]? fileType, Mp4Box movie)
    {
        byte[] header = movie.Required("mvhd").Payload!;
        uint movieTimescale = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(header[0] == 1 ? 20 : 12));
        if (movieTimescale == 0) throw new InvalidDataException("The movie has no timescale.");
        Mp4Track? video = null, audio = null;
        foreach (Mp4Box trak in movie.Children!.Where(child => child.Type == "trak"))
        {
            Mp4Track track = ReadTrack(trak, movieTimescale);
            if (track.IsVideo)
            {
                if (video is not null) throw new NotSupportedException("Movies with more than one video track are trimmed by re-encoding.");
                video = track;
            }
            else if (track.Handler == "soun")
            {
                if (audio is not null) throw new NotSupportedException("Movies with more than one audio track are trimmed by re-encoding.");
                audio = track;
            }
            else throw new NotSupportedException($"Movies with a '{track.Handler}' track are trimmed by re-encoding.");
        }
        if (video is null || video.Count == 0) throw new NotSupportedException("The movie has no video samples.");
        if (!video.Sync[0]) throw new NotSupportedException("The movie does not start with a keyframe.");
        return new Mp4MovieIndex(fileType, movie, movieTimescale, video, audio);
    }

    private static Mp4Track ReadTrack(Mp4Box trak, uint movieTimescale)
    {
        Mp4Box media = trak.Required("mdia");
        byte[] handlerBox = media.Required("hdlr").Payload!;
        string handler = Encoding.ASCII.GetString(handlerBox, 8, 4);
        byte[] mediaHeader = media.Required("mdhd").Payload!;
        uint timescale = BinaryPrimitives.ReadUInt32BigEndian(mediaHeader.AsSpan(mediaHeader[0] == 1 ? 20 : 12));
        if (timescale == 0) throw new InvalidDataException("A track has no timescale.");
        Mp4Box table = media.Required("minf").Required("stbl");
        byte[] descriptions = table.Required("stsd").Payload!;
        if (BinaryPrimitives.ReadUInt32BigEndian(descriptions.AsSpan(4)) != 1) throw new NotSupportedException("A track changes format midway.");
        if (table.Child("ctts")?.Payload is byte[] compositionOffsets && HasNonZeroOffsets(compositionOffsets))
            throw new NotSupportedException("The movie uses B-frames, so it is trimmed by re-encoding.");
        uint[] durations = ExpandTimeToSample(table.Required("stts").Payload!);
        int[] sizes = ReadSampleSizes(table.Required("stsz").Payload!);
        if (durations.Length < sizes.Length) throw new InvalidDataException("A track's timing table is short.");
        long[] offsets = ReadSampleOffsets(table, sizes);
        var decodeTimes = new long[sizes.Length];
        long time = 0;
        for (int sample = 0; sample < sizes.Length; sample++) { decodeTimes[sample] = time; time += durations[sample]; }
        bool hasSyncTable = table.Child("stss") is not null;
        bool[] sync = ReadSync(table.Child("stss")?.Payload, sizes.Length);
        (long mediaTimeOffset, double emptySeconds) = ReadEditList(trak.Child("edts")?.Child("elst")?.Payload, movieTimescale);
        return new Mp4Track(trak, handler, timescale, offsets, sizes, decodeTimes, durations[..sizes.Length], sync, hasSyncTable, mediaTimeOffset, emptySeconds);
    }

    private static bool HasNonZeroOffsets(byte[] payload)
    {
        uint count = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4));
        for (int entry = 0; entry < count; entry++)
            if (BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(8 + entry * 8 + 4)) != 0) return true;
        return false;
    }

    private static uint[] ExpandTimeToSample(byte[] payload)
    {
        uint entries = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4));
        var durations = new List<uint>();
        for (int entry = 0; entry < entries; entry++)
        {
            uint count = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(8 + entry * 8));
            uint delta = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(12 + entry * 8));
            for (uint repeat = 0; repeat < count; repeat++) durations.Add(delta);
        }
        return durations.ToArray();
    }

    private static int[] ReadSampleSizes(byte[] payload)
    {
        uint uniform = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4));
        int count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(8)));
        var sizes = new int[count];
        for (int sample = 0; sample < count; sample++)
            sizes[sample] = checked((int)(uniform != 0 ? uniform : BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(12 + sample * 4))));
        return sizes;
    }

    private static long[] ReadSampleOffsets(Mp4Box table, int[] sizes)
    {
        long[] chunkOffsets = table.Child("co64")?.Payload is byte[] large
            ? Enumerable.Range(0, (int)BinaryPrimitives.ReadUInt32BigEndian(large.AsSpan(4))).Select(chunk => (long)BinaryPrimitives.ReadUInt64BigEndian(large.AsSpan(8 + chunk * 8))).ToArray()
            : Enumerable.Range(0, (int)BinaryPrimitives.ReadUInt32BigEndian(table.Required("stco").Payload!.AsSpan(4))).Select(chunk => (long)BinaryPrimitives.ReadUInt32BigEndian(table.Child("stco")!.Payload!.AsSpan(8 + chunk * 4))).ToArray();
        byte[] sampleToChunk = table.Required("stsc").Payload!;
        int entries = (int)BinaryPrimitives.ReadUInt32BigEndian(sampleToChunk.AsSpan(4));
        if (entries == 0 && sizes.Length > 0) throw new InvalidDataException("A track has no sample-to-chunk table.");
        var offsets = new long[sizes.Length];
        int sample = 0;
        for (int entry = 0; entry < entries && sample < sizes.Length; entry++)
        {
            int firstChunk = (int)BinaryPrimitives.ReadUInt32BigEndian(sampleToChunk.AsSpan(8 + entry * 12)) - 1;
            int perChunk = (int)BinaryPrimitives.ReadUInt32BigEndian(sampleToChunk.AsSpan(12 + entry * 12));
            int nextFirst = entry + 1 < entries ? (int)BinaryPrimitives.ReadUInt32BigEndian(sampleToChunk.AsSpan(8 + (entry + 1) * 12)) - 1 : chunkOffsets.Length;
            for (int chunk = firstChunk; chunk < nextFirst && chunk < chunkOffsets.Length && sample < sizes.Length; chunk++)
            {
                long offset = chunkOffsets[chunk];
                for (int index = 0; index < perChunk && sample < sizes.Length; index++)
                {
                    offsets[sample] = offset;
                    offset += sizes[sample];
                    sample++;
                }
            }
        }
        if (sample < sizes.Length) throw new InvalidDataException("A track's chunk table is short.");
        return offsets;
    }

    private static bool[] ReadSync(byte[]? payload, int count)
    {
        var sync = new bool[count];
        if (payload is null) { Array.Fill(sync, true); return sync; }
        uint entries = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4));
        for (int entry = 0; entry < entries; entry++)
        {
            long number = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(8 + entry * 4)) - 1L;
            if (number >= 0 && number < count) sync[number] = true;
        }
        return sync;
    }

    /// <summary>Accepts leading empty edits followed by one normal-speed edit; returns its media-time offset and the empty lead.</summary>
    private static (long MediaTime, double EmptySeconds) ReadEditList(byte[]? payload, uint movieTimescale)
    {
        if (payload is null) return (0, 0);
        bool wide = payload[0] == 1;
        uint entries = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4));
        int entrySize = wide ? 20 : 12, position = 8;
        double empty = 0;
        long? mediaTime = null;
        for (int entry = 0; entry < entries; entry++, position += entrySize)
        {
            long segment = wide ? (long)BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(position)) : BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(position));
            long time = wide ? BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(position + 8)) : BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(position + 4));
            short rate = BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(position + (wide ? 16 : 8)));
            if (time == -1 && mediaTime is null) { empty += segment / (double)movieTimescale; continue; }
            if (mediaTime is not null || rate != 1) throw new NotSupportedException("The movie has an edit list that is trimmed by re-encoding.");
            mediaTime = time;
        }
        return (mediaTime ?? 0, empty);
    }
}

/// <summary>What a lossless trim will actually keep: kept stretches whose starts were moved back to keyframes.</summary>
internal sealed record LosslessTrimPlan(IReadOnlyList<KeptRange> Ranges, double InSnapSeconds);

/// <summary>
/// Pure planning of a stream-copy trim. Video can only start at a keyframe, so the first kept stretch starts at the
/// keyframe at or before the in point (shown to the user as "snaps"). A later stretch after a cut must already start
/// on a keyframe; otherwise the cut-out picture would come back, so that trim is re-encoded instead.
/// </summary>
internal static class LosslessTrimPlanner
{
    internal static LosslessTrimPlan? Plan(IReadOnlyList<double> keyframeTimes, IReadOnlyList<KeptRange> ranges, double frameDuration, out string? reason)
    {
        reason = null;
        List<KeptRange> merged = Merge(ranges);
        if (merged.Count == 0 || keyframeTimes.Count == 0) { reason = "Nothing is kept."; return null; }
        double tolerance = Math.Max(1e-4, frameDuration / 2);
        var planned = new List<KeptRange>();
        double output = 0, snap = 0;
        for (int index = 0; index < merged.Count; index++)
        {
            KeptRange range = merged[index];
            double keyframe = keyframeTimes.Where(time => time <= range.Start + tolerance).DefaultIfEmpty(keyframeTimes[0]).Max();
            if (index > 0 && range.Start - keyframe > tolerance)
            {
                reason = $"A cut ends at {RecordingTimeText.Tenths(range.Start)}, between keyframes, so trimming re-encodes to keep it exact.";
                return null;
            }
            double start = index == 0 ? Math.Min(keyframe, range.Start) : range.Start;
            if (index == 0) snap = Math.Max(0, range.Start - start);
            planned.Add(new KeptRange(start, range.End, output));
            output += range.End - start;
        }
        return new LosslessTrimPlan(planned, snap);
    }

    /// <summary>Joins kept stretches that touch (a split without a cut is not a cut).</summary>
    internal static List<KeptRange> Merge(IReadOnlyList<KeptRange> ranges)
    {
        var merged = new List<KeptRange>();
        foreach (KeptRange range in ranges.Where(range => range.Duration > 1e-6))
        {
            if (merged.Count > 0 && range.Start - merged[^1].End < 1e-6) merged[^1] = merged[^1] with { End = range.End };
            else merged.Add(range);
        }
        double output = 0;
        for (int index = 0; index < merged.Count; index++)
        {
            merged[index] = merged[index] with { OutputStart = output };
            output += merged[index].Duration;
        }
        return merged;
    }
}

/// <summary>
/// Writes a trimmed MP4 by copying the compressed samples of the kept stretches (no decoding or re-encoding), with a
/// rebuilt index placed before the media data. Audio samples are chosen so each stretch's audio stays as long as its
/// video, keeping sound in sync across cuts to within one audio frame.
/// </summary>
internal static class Mp4Remuxer
{
    private const double ChunkSeconds = 1.0;

    internal static void Write(Mp4MovieIndex index, IReadOnlyList<KeptRange> ranges, string sourcePath, string destinationPath, IProgress<double>? progress, CancellationToken cancellation)
    {
        List<int> videoSamples = SelectVideo(index.Video, ranges);
        if (videoSamples.Count == 0) throw new InvalidOperationException("Nothing is kept, so there is nothing to trim.");
        List<int>? audioSamples = index.Audio is Mp4Track audio ? SelectAudio(audio, index.Video, videoSamples, ranges) : null;
        var tracks = new List<(Mp4Track Track, List<int> Samples)> { (index.Video, videoSamples) };
        if (index.Audio is not null && audioSamples is not null) tracks.Add((index.Audio, audioSamples));
        List<(int Track, int First, int Count)> chunks = PlanChunks(tracks);
        long dataBytes = tracks.Sum(item => item.Samples.Sum(sample => (long)item.Track.Sizes[sample]));
        byte[] fileType = index.FileType ?? DefaultFileType();
        long fileTypeBytes = fileType.Length + 8;
        bool large = dataBytes + 64L * 1024 * 1024 > uint.MaxValue;
        byte[] probe = BuildMovie(index, tracks, chunks, 0, large).Serialize();
        int mdatHeader = large ? 16 : 8;
        long dataStart = fileTypeBytes + probe.Length + mdatHeader;
        byte[] movie = BuildMovie(index, tracks, chunks, dataStart, large).Serialize();
        if (movie.Length != probe.Length) throw new InvalidOperationException("The movie index changed size while it was written.");

        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        new Mp4Box("ftyp", fileType, null).WriteTo(destination);
        destination.Write(movie);
        WriteMediaDataHeader(destination, dataBytes, large);
        byte[] buffer = new byte[1 << 20];
        long copied = 0;
        foreach ((int track, int first, int count) in chunks)
        {
            cancellation.ThrowIfCancellationRequested();
            (Mp4Track sourceTrack, List<int> samples) = tracks[track];
            for (int position = first; position < first + count; position++)
            {
                int sample = samples[position];
                int size = sourceTrack.Sizes[sample];
                if (buffer.Length < size) buffer = new byte[size];
                source.Position = sourceTrack.Offsets[sample];
                source.ReadExactly(buffer, 0, size);
                destination.Write(buffer, 0, size);
                copied += size;
            }
            progress?.Report(dataBytes == 0 ? 1 : copied / (double)dataBytes);
        }
        destination.Flush(true);
    }

    /// <summary>Video samples presented within each kept stretch (each stretch starts on a keyframe).</summary>
    internal static List<int> SelectVideo(Mp4Track video, IReadOnlyList<KeptRange> ranges)
    {
        var selected = new List<int>();
        double epsilon = 0.5 / video.Timescale;
        foreach (KeptRange range in ranges)
        {
            for (int sample = 0; sample < video.Count; sample++)
            {
                double time = video.TimeOf(sample);
                if (time >= range.Start - epsilon && time < range.End - epsilon) selected.Add(sample);
            }
        }
        return selected;
    }

    /// <summary>
    /// For each stretch, audio from its start onward, taking frames only while the audio written so far is no longer
    /// than the video written so far (to within half an audio frame).
    /// </summary>
    private static List<int> SelectAudio(Mp4Track audio, Mp4Track video, List<int> videoSamples, IReadOnlyList<KeptRange> ranges)
    {
        var selected = new List<int>();
        double videoOutput = 0, audioOutput = 0;
        int videoPosition = 0;
        foreach (KeptRange range in ranges)
        {
            double epsilon = 0.5 / video.Timescale;
            while (videoPosition < videoSamples.Count && video.TimeOf(videoSamples[videoPosition]) < range.End - epsilon)
                videoOutput += video.DurationOf(videoSamples[videoPosition++]);
            int sample = 0;
            while (sample < audio.Count && audio.TimeOf(sample) + audio.DurationOf(sample) / 2 < range.Start) sample++;
            while (sample < audio.Count && audioOutput + audio.DurationOf(sample) / 2 <= videoOutput)
            {
                selected.Add(sample);
                audioOutput += audio.DurationOf(sample);
                sample++;
            }
        }
        return selected;
    }

    /// <summary>Interleaves the tracks in chunks of about one second of output each.</summary>
    private static List<(int Track, int First, int Count)> PlanChunks(List<(Mp4Track Track, List<int> Samples)> tracks)
    {
        var positions = new int[tracks.Count];
        var elapsed = new double[tracks.Count];
        var chunks = new List<(int, int, int)>();
        for (double window = ChunkSeconds; positions.Select((position, track) => position < tracks[track].Samples.Count).Any(left => left); window += ChunkSeconds)
        {
            for (int track = 0; track < tracks.Count; track++)
            {
                int first = positions[track];
                while (positions[track] < tracks[track].Samples.Count && elapsed[track] < window)
                {
                    elapsed[track] += tracks[track].Track.DurationOf(tracks[track].Samples[positions[track]]);
                    positions[track]++;
                }
                if (positions[track] > first) chunks.Add((track, first, positions[track] - first));
            }
        }
        return chunks;
    }

    private static Mp4Box BuildMovie(Mp4MovieIndex index, List<(Mp4Track Track, List<int> Samples)> tracks, List<(int Track, int First, int Count)> chunks, long dataStart, bool large)
    {
        Mp4Box movie = index.Movie.Clone();
        var chunkOffsets = new List<long>[tracks.Count];
        for (int track = 0; track < tracks.Count; track++) chunkOffsets[track] = [];
        long offset = dataStart;
        foreach ((int track, int first, int count) in chunks)
        {
            chunkOffsets[track].Add(offset);
            for (int position = first; position < first + count; position++) offset += tracks[track].Track.Sizes[tracks[track].Samples[position]];
        }
        double longest = 0;
        var trakBoxes = movie.Children!.Where(child => child.Type == "trak").ToList();
        for (int track = 0; track < tracks.Count; track++)
        {
            (Mp4Track source, List<int> samples) = tracks[track];
            int trakIndex = index.Movie.Children!.Where(child => child.Type == "trak").ToList().IndexOf(source.Trak);
            Mp4Box trak = trakBoxes[trakIndex];
            long mediaDuration = samples.Sum(sample => (long)source.Durations[sample]);
            double seconds = mediaDuration / (double)source.Timescale;
            longest = Math.Max(longest, seconds);
            trak.Children!.RemoveAll(child => child.Type == "edts");
            SetDuration(trak.Required("tkhd"), (ulong)Math.Round(seconds * index.MovieTimescale), versionOneOffset: 28, versionZeroOffset: 20);
            Mp4Box media = trak.Required("mdia");
            SetDuration(media.Required("mdhd"), (ulong)mediaDuration, versionOneOffset: 24, versionZeroOffset: 16);
            Mp4Box table = media.Required("minf").Required("stbl");
            List<int> chunkSizes = chunks.Where(chunk => chunk.Track == track).Select(chunk => chunk.Count).ToList();
            RebuildSampleTable(table, source, samples, chunkSizes, chunkOffsets[track], large);
        }
        SetDuration(movie.Required("mvhd"), (ulong)Math.Round(longest * index.MovieTimescale), versionOneOffset: 24, versionZeroOffset: 16);
        return movie;
    }

    private static void SetDuration(Mp4Box box, ulong duration, int versionOneOffset, int versionZeroOffset)
    {
        byte[] payload = box.Payload!.ToArray();
        if (payload[0] == 1) BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(versionOneOffset), duration);
        else BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(versionZeroOffset), (uint)Math.Min(duration, uint.MaxValue));
        box.Payload = payload;
    }

    private static void RebuildSampleTable(Mp4Box table, Mp4Track track, List<int> samples, List<int> chunkSizes, List<long> chunkOffsets, bool large)
    {
        var keep = new HashSet<string> { "stsd" };
        List<Mp4Box> others = table.Children!.Where(child => keep.Contains(child.Type)).ToList();
        table.Children!.Clear();
        table.Children.AddRange(others);
        table.Children.Add(new Mp4Box("stts", TimeToSample(samples.Select(sample => track.Durations[sample]).ToList()), null));
        if (track.HasSyncTable) table.Children.Add(new Mp4Box("stss", SyncSamples(samples.Select(sample => track.Sync[sample]).ToList()), null));
        table.Children.Add(new Mp4Box("stsc", SampleToChunk(chunkSizes), null));
        table.Children.Add(new Mp4Box("stsz", SampleSizes(samples.Select(sample => track.Sizes[sample]).ToList()), null));
        table.Children.Add(large ? new Mp4Box("co64", ChunkOffsets64(chunkOffsets), null) : new Mp4Box("stco", ChunkOffsets32(chunkOffsets), null));
    }

    private static byte[] TimeToSample(List<uint> durations)
    {
        var runs = new List<(uint Count, uint Delta)>();
        foreach (uint duration in durations)
        {
            if (runs.Count > 0 && runs[^1].Delta == duration) runs[^1] = (runs[^1].Count + 1, duration);
            else runs.Add((1, duration));
        }
        byte[] payload = new byte[8 + runs.Count * 8];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)runs.Count);
        for (int run = 0; run < runs.Count; run++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8 + run * 8), runs[run].Count);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12 + run * 8), runs[run].Delta);
        }
        return payload;
    }

    private static byte[] SyncSamples(List<bool> sync)
    {
        int[] numbers = sync.Select((isSync, position) => isSync ? position + 1 : 0).Where(number => number > 0).ToArray();
        byte[] payload = new byte[8 + numbers.Length * 4];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)numbers.Length);
        for (int entry = 0; entry < numbers.Length; entry++) BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8 + entry * 4), (uint)numbers[entry]);
        return payload;
    }

    private static byte[] SampleToChunk(List<int> chunkSizes)
    {
        var runs = new List<(int FirstChunk, int PerChunk)>();
        for (int chunk = 0; chunk < chunkSizes.Count; chunk++)
            if (runs.Count == 0 || runs[^1].PerChunk != chunkSizes[chunk]) runs.Add((chunk + 1, chunkSizes[chunk]));
        byte[] payload = new byte[8 + runs.Count * 12];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)runs.Count);
        for (int run = 0; run < runs.Count; run++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8 + run * 12), (uint)runs[run].FirstChunk);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12 + run * 12), (uint)runs[run].PerChunk);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16 + run * 12), 1);
        }
        return payload;
    }

    private static byte[] SampleSizes(List<int> sizes)
    {
        bool uniform = sizes.Count > 0 && sizes.All(size => size == sizes[0]);
        byte[] payload = new byte[12 + (uniform ? 0 : sizes.Count * 4)];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), uniform ? (uint)sizes[0] : 0);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8), (uint)sizes.Count);
        if (!uniform)
            for (int sample = 0; sample < sizes.Count; sample++) BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12 + sample * 4), (uint)sizes[sample]);
        return payload;
    }

    private static byte[] ChunkOffsets32(List<long> offsets)
    {
        byte[] payload = new byte[8 + offsets.Count * 4];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)offsets.Count);
        for (int chunk = 0; chunk < offsets.Count; chunk++) BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8 + chunk * 4), (uint)offsets[chunk]);
        return payload;
    }

    private static byte[] ChunkOffsets64(List<long> offsets)
    {
        byte[] payload = new byte[8 + offsets.Count * 8];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)offsets.Count);
        for (int chunk = 0; chunk < offsets.Count; chunk++) BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(8 + chunk * 8), (ulong)offsets[chunk]);
        return payload;
    }

    private static void WriteMediaDataHeader(Stream stream, long dataBytes, bool large)
    {
        Span<byte> header = stackalloc byte[16];
        if (large)
        {
            BinaryPrimitives.WriteUInt32BigEndian(header, 1);
            Encoding.ASCII.GetBytes("mdat", header[4..]);
            BinaryPrimitives.WriteUInt64BigEndian(header[8..], (ulong)(dataBytes + 16));
            stream.Write(header);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(dataBytes + 8));
            Encoding.ASCII.GetBytes("mdat", header[4..]);
            stream.Write(header[..8]);
        }
    }

    private static byte[] DefaultFileType()
    {
        byte[] payload = new byte[16];
        Encoding.ASCII.GetBytes("isom", payload);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), 0x200);
        Encoding.ASCII.GetBytes("isommp42", payload.AsSpan(8));
        return payload;
    }
}
