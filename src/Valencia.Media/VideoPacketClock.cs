using System.Buffers.Binary;
using System.Security.Cryptography;
using FFmpeg.AutoGen;

namespace Valencia.Media;

/// <summary>Checks the original and proxy packet presentation clocks without decoding into pixel buffers.</summary>
internal static unsafe class VideoPacketClock
{
    internal static (long Packets, string Hash) Read(string path, int streamIndex, CancellationToken cancellation)
    {
        const int ReorderWindow = 64;
        AVFormatContext* source = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        if (packet == null) throw new OutOfMemoryException("FFmpeg packet allocation failed.");
        try
        {
            FfmpegRuntime.Check(ffmpeg.avformat_open_input(&source, path, null, null), "Open proxy clock source");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var pending = new PriorityQueue<long, long>(ReorderWindow + 1);
            long count = 0, last = long.MinValue;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                int result = ffmpeg.av_read_frame(source, packet);
                if (result == ffmpeg.AVERROR_EOF) break;
                FfmpegRuntime.Check(result, "Read proxy clock packet");
                try
                {
                    if (packet->stream_index != streamIndex) continue;
                    if (packet->pts == ffmpeg.AV_NOPTS_VALUE)
                        throw new NotSupportedException("Video packet lacks presentation timestamps; use original media.");
                    pending.Enqueue(packet->pts, packet->pts);
                    if (pending.Count > ReorderWindow) Append(pending.Dequeue());
                }
                finally { ffmpeg.av_packet_unref(packet); }
            }
            while (pending.TryDequeue(out var pts, out _)) Append(pts);
            if (count == 0) throw new InvalidDataException("Video packet clock is empty.");
            return (count, Convert.ToHexString(hash.GetHashAndReset()));

            void Append(long pts)
            {
                if (pts < last) throw new NotSupportedException("Video packet reorder exceeds the proxy validation window.");
                Span<byte> bytes = stackalloc byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(bytes, pts);
                hash.AppendData(bytes);
                last = pts;
                count++;
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avformat_close_input(&source);
        }
    }
}
