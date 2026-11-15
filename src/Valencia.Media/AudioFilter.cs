using FFmpeg.AutoGen;

namespace Valencia.Media;

/// <summary>One worker-owned libavfilter graph. FFmpeg owns FFT denoising; no DSP or neural runtime is recreated.</summary>
internal sealed unsafe class AudioFilter : IDisposable
{
    private AVFilterGraph* graph;
    private AVFilterContext* source;
    private AVFilterContext* sink;
    private readonly int rate;
    private readonly int latency;
    private long through;
    private bool ended;

    public AudioFilter(int rate, double reduction)
    {
        this.rate = rate;
        // FFmpeg 7 afftdn shifts its three-hop window by one rate/80 hop.
        // Signal latency is two integer hops; independently impulse-tested at supported rates.
        latency = 2 * (rate / 80);
        graph = ffmpeg.avfilter_graph_alloc();
        if (graph == null) throw new OutOfMemoryException("Audio filter graph allocation failed.");
        graph->nb_threads = 1;
        try
        {
            AVFilterContext* input = null; AVFilterContext* output = null;
            FfmpegRuntime.Check(ffmpeg.avfilter_graph_create_filter(&input, ffmpeg.avfilter_get_by_name("abuffer"), "input",
                $"time_base=1/{rate}:sample_rate={rate}:sample_fmt=flt:channel_layout=stereo", null, graph), "Create audio filter input");
            source = input;
            FfmpegRuntime.Check(ffmpeg.avfilter_graph_create_filter(&output, ffmpeg.avfilter_get_by_name("abuffersink"), "output", null, null, graph), "Create audio filter output");
            sink = output;
            AVFilterInOut* inputs = ffmpeg.avfilter_inout_alloc(); AVFilterInOut* outputs = ffmpeg.avfilter_inout_alloc();
            try
            {
                outputs->name = ffmpeg.av_strdup("in"); outputs->filter_ctx = source; outputs->pad_idx = 0;
                inputs->name = ffmpeg.av_strdup("out"); inputs->filter_ctx = sink; inputs->pad_idx = 0;
                string chain = FormattableString.Invariant($"afftdn=nr={reduction}:nf=-45:tn=0,aformat=sample_fmts=flt:sample_rates={rate}:channel_layouts=stereo");
                FfmpegRuntime.Check(ffmpeg.avfilter_graph_parse_ptr(graph, chain, &inputs, &outputs, null), "Configure FFT noise suppression");
                FfmpegRuntime.Check(ffmpeg.avfilter_graph_config(graph, null), "Start audio filters");
            }
            finally { ffmpeg.avfilter_inout_free(&inputs); ffmpeg.avfilter_inout_free(&outputs); }
        }
        catch { Dispose(); throw; }
    }

    public void Push(float[] data, long pts)
    {
        through = checked(pts + data.Length / 2);
        AVFrame* frame = ffmpeg.av_frame_alloc();
        try
        {
            frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLT; frame->sample_rate = rate; frame->nb_samples = data.Length / 2; frame->pts = pts;
            ffmpeg.av_channel_layout_default(&frame->ch_layout, 2);
            FfmpegRuntime.Check(ffmpeg.av_frame_get_buffer(frame, 0), "Allocate audio filter block");
            fixed (float* samples = data) Buffer.MemoryCopy(samples, frame->data[0], data.Length * sizeof(float), data.Length * sizeof(float));
            FfmpegRuntime.Check(ffmpeg.av_buffersrc_add_frame_flags(source, frame, 0), "Feed audio cleanup");
        }
        finally { ffmpeg.av_frame_free(&frame); }
    }
    public void Finish()
    {
        if (ended) return;
        ended = true;
        // afftdn does not emit its internal window at EOF without further input.
        // Supply bounded silence; the clip evaluator discards everything beyond the source range.
        Push(new float[(latency + rate / 80) * 2], through);
        FfmpegRuntime.Check(ffmpeg.av_buffersrc_add_frame_flags(source, null, 0), "Drain audio cleanup");
    }
    public float[]? Pull(out long pts)
    {
        AVFrame* frame = ffmpeg.av_frame_alloc();
        try
        {
            int result = ffmpeg.av_buffersink_get_frame(sink, frame);
            pts = 0;
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) return null;
            FfmpegRuntime.Check(result, "Read audio cleanup");
            pts = checked(frame->pts - latency);
            float[] output = new float[frame->nb_samples * 2];
            fixed (float* samples = output) Buffer.MemoryCopy(frame->data[0], samples, output.Length * sizeof(float), output.Length * sizeof(float));
            return output;
        }
        finally { ffmpeg.av_frame_free(&frame); }
    }
    public void Dispose() { AVFilterGraph* owned = graph; graph = null; ffmpeg.avfilter_graph_free(&owned); }
}
