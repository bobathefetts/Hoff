#include <stdint.h>
#include <string.h>
#include <libavcodec/avcodec.h>
#include <libavcodec/codec_internal.h>
#include <libavformat/avformat.h>
#include <libavutil/display.h>
#include <libavutil/mathematics.h>
#include <libswscale/swscale.h>

extern const FFCodec ff_libdav1d_decoder;

typedef struct {
    int (*read)(void *, uint8_t *, int);
    int64_t (*seek)(void *, int64_t, int);
    void *state;
} Input;
typedef uint8_t *(*AllocatePixels)(int width, int height, int stride, int capacity);

static int read_media(void *opaque, uint8_t *buffer, int count) {
    Input *input = opaque;
    int result = input->read(input->state, buffer, count);
    return result == 0 ? AVERROR_EOF : result < 0 ? AVERROR(EIO) : result;
}

static int64_t seek_media(void *opaque, int64_t offset, int whence) {
    Input *input = opaque;
    int64_t result = input->seek(input->state, offset, whence & ~AVSEEK_FORCE);
    return result < 0 ? AVERROR(EIO) : result;
}

static int deny_external(AVFormatContext *format, AVIOContext **io, const char *url, int flags, AVDictionary **options) {
    return AVERROR(EACCES);
}

/* Buffers are caller-owned. The return code is a libav error, or zero. */
__declspec(dllexport) int mcr_decode(const char *name, Input *input, AllocatePixels allocate, int side,
        int max_pixels, int *stride_out,
        int *width, int *height, int *source_width, int *source_height, int *rotation, int percent) {
    const AVInputFormat *demuxer = av_find_input_format(name);
    if (!demuxer || !allocate || side < 1 || side > 8192 ||
            max_pixels < 1 || max_pixels > 8 * 1024 * 1024 || percent < 0 || percent > 80) return AVERROR(EINVAL);
    AVFormatContext *format = avformat_alloc_context();
    AVCodecContext *decoder = NULL;
    AVPacket *packet = NULL;
    AVFrame *frame = NULL;
    struct SwsContext *scaler = NULL;
    uint8_t *buffer = av_malloc(32768);
    AVIOContext *io = buffer ? avio_alloc_context(buffer, 32768, 0, input, read_media, NULL, seek_media) : NULL;
    int rc = AVERROR(ENOMEM);
    if (!format || !io) goto done;
    format->pb = io;
    format->flags |= AVFMT_FLAG_CUSTOM_IO;
    format->io_open = deny_external;
    format->probesize = 2 * 1024 * 1024;
    format->max_analyze_duration = 2 * AV_TIME_BASE;
    format->max_streams = 64;
    av_log_set_level(AV_LOG_QUIET);
    rc = avformat_open_input(&format, NULL, demuxer, NULL);
    if (rc < 0) goto done;
    rc = avformat_find_stream_info(format, NULL);
    if (rc < 0) goto done;
    const AVCodec *codec = NULL;
    int stream = av_find_best_stream(format, AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
    if (stream < 0) { rc = stream; goto done; }
    AVCodecParameters *parameters = format->streams[stream]->codecpar;
    if (parameters->codec_id == AV_CODEC_ID_AV1) codec = &ff_libdav1d_decoder.p;
    decoder = avcodec_alloc_context3(codec);
    packet = av_packet_alloc();
    frame = av_frame_alloc();
    if (!decoder || !packet || !frame) { rc = AVERROR(ENOMEM); goto done; }
    if ((rc = avcodec_parameters_to_context(decoder, parameters)) < 0) goto done;
    decoder->max_pixels = 32000000;
    decoder->thread_count = 1;
    if ((rc = avcodec_open2(decoder, codec, NULL)) < 0) goto done;
    int64_t target = AV_NOPTS_VALUE;
    if (percent) {
        AVStream *track = format->streams[stream];
        int gif = !strcmp(name, "gif");
        int64_t duration = track->duration;
        if (duration <= 0 && format->duration > 0)
            duration = av_rescale_q(format->duration, AV_TIME_BASE_Q, track->time_base);
        if (duration <= 0)
            duration = av_rescale_q((gif ? 3 : 10) * AV_TIME_BASE, AV_TIME_BASE_Q, track->time_base);
        int64_t start = track->start_time == AV_NOPTS_VALUE ? 0 : track->start_time;
        target = av_sat_add64(start, av_rescale(duration, percent, 100));
        /* GIF frames can depend on prior canvas updates. Decode the first loop from its start. */
        if (!gif && av_seek_frame(format, stream, target, AVSEEK_FLAG_BACKWARD) >= 0)
            avcodec_flush_buffers(decoder);
    }
    rc = AVERROR_INVALIDDATA;
    for (int i = 0; i < 1500; ++i) {
        int read = av_read_frame(format, packet);
        if (read < 0) {
            if (read != AVERROR_EOF) { rc = read; goto done; }
            rc = avcodec_send_packet(decoder, NULL);
        } else {
            if (packet->stream_index != stream) { av_packet_unref(packet); continue; }
            rc = avcodec_send_packet(decoder, packet);
            av_packet_unref(packet);
        }
        if (rc < 0 && rc != AVERROR(EAGAIN) && rc != AVERROR_EOF) goto done;
        while ((rc = avcodec_receive_frame(decoder, frame)) == 0) {
            if (target == AV_NOPTS_VALUE || frame->best_effort_timestamp >= target)
                goto render;
            av_frame_unref(frame);
        }
        if (rc == AVERROR_EOF || (rc == AVERROR(EAGAIN) && read == AVERROR_EOF)) break;
        if (rc != AVERROR(EAGAIN)) goto done;
    }
    rc = percent ? 1 : AVERROR_INVALIDDATA;
    goto done;
render:
    if (frame->width < 1 || frame->height < 1 || (int64_t)frame->width * frame->height > 32000000) {
        rc = AVERROR_INVALIDDATA; goto done;
    }
    int w = frame->width, h = frame->height;
    AVRational aspect = av_guess_sample_aspect_ratio(format, format->streams[stream], frame);
    if (aspect.num > 0 && aspect.den > 0) {
        int64_t adjusted = av_rescale(w, aspect.num, aspect.den);
        if (adjusted > 0 && adjusted <= (int64_t)w * 8 && adjusted * 8 >= w &&
                adjusted * h <= 200000000) w = (int)adjusted;
    }
    *source_width = w;
    *source_height = h;
    int longest = w > h ? w : h;
    int lo = 1, hi = FFMIN(side, longest);
    while (lo < hi) {
        int middle = lo + (hi - lo + 1) / 2;
        int64_t area = FFMAX(1, av_rescale(w, middle, longest)) *
                       FFMAX(1, av_rescale(h, middle, longest));
        if (area <= max_pixels) lo = middle;
        else hi = middle - 1;
    }
    *width = (int)FFMAX(1, av_rescale(w, lo, longest));
    *height = (int)FFMAX(1, av_rescale(h, lo, longest));
    *rotation = 0;
    const AVPacketSideData *matrix = av_packet_side_data_get(parameters->coded_side_data,
            parameters->nb_coded_side_data, AV_PKT_DATA_DISPLAYMATRIX);
    if (matrix && matrix->size >= 9 * sizeof(int32_t)) {
        double angle = av_display_rotation_get((const int32_t *)matrix->data);
        if (isfinite(angle)) *rotation = (int)(-angle + (angle > 0 ? -0.5 : 0.5));
    }
    /* Align rows for swscale without reserving a square the size of the longest edge. */
    int packed_stride = (*width * 4 + 63) & ~63;
    int64_t capacity = (int64_t)packed_stride * *height + 64;
    if (capacity > (int64_t)max_pixels * 4 + (int64_t)side * 63 + 64) {
        rc = AVERROR(EINVAL); goto done;
    }
    uint8_t *pixels = allocate(*width, *height, packed_stride, (int)capacity);
    if (!pixels) { rc = AVERROR(ENOMEM); goto done; }
    *stride_out = packed_stride;
    scaler = sws_getContext(frame->width, frame->height, frame->format, *width, *height,
                            AV_PIX_FMT_BGRA, SWS_BILINEAR, NULL, NULL, NULL);
    if (!scaler) { rc = AVERROR(ENOMEM); goto done; }
    uint8_t *data[4] = {pixels, NULL, NULL, NULL};
    int stride[4] = {packed_stride, 0, 0, 0};
    rc = sws_scale(scaler, (const uint8_t *const *)frame->data, frame->linesize, 0, frame->height, data, stride);
    rc = rc == *height ? 0 : AVERROR_INVALIDDATA;
done:
    sws_freeContext(scaler);
    av_frame_free(&frame);
    av_packet_free(&packet);
    avcodec_free_context(&decoder);
    avformat_close_input(&format);
    if (io) av_freep(&io->buffer);
    else av_free(buffer);
    avio_context_free(&io);
    return rc;
}
