using System.Globalization;
using System.IO;

namespace ICC.CE.ENBX;

internal static class CoursewareRange
{
    public static bool TryParse(string? range, long length, out long start, out long count)
    {
        start = 0;
        count = length;
        if (range == null) return true;
        if (!range.StartsWith("bytes=", StringComparison.Ordinal) || length == 0) return false;
        var parts = range[6..].Split('-');
        if (parts.Length != 2) return false;
        static bool Number(string value, out long number) => long.TryParse(value, NumberStyles.None,
            CultureInfo.InvariantCulture, out number);
        if (parts[0].Length == 0)
        {
            if (!Number(parts[1], out var suffix) || suffix <= 0) return false;
            count = Math.Min(suffix, length);
            start = length - count;
            return true;
        }
        if (!Number(parts[0], out start) || start >= length) return false;
        var end = length - 1;
        if (parts[1].Length > 0 && (!Number(parts[1], out end) || end < start)) return false;
        count = Math.Min(end, length - 1) - start + 1;
        return true;
    }
}

/// <summary>仅暴露指定区间，防止 Range 响应继续读出文件余下部分。</summary>
internal sealed class CoursewareRangeStream : Stream
{
    private readonly FileStream _file;
    private readonly long _start;
    private readonly long _length;
    public CoursewareRangeStream(string path, long start, long length)
    {
        _file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        _start = start;
        _length = length;
        _file.Position = start;
    }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _file.Position - _start; set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count)
        => _file.Read(buffer, offset, (int)Math.Min(count, Math.Max(0, _length - Position)));
    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset,
            SeekOrigin.End => _length + offset, _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        if (position < 0 || position > _length) throw new IOException("区间越界");
        _file.Position = _start + position;
        return position;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _file.Dispose(); base.Dispose(disposing); }
}
