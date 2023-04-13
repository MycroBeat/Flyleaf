﻿using FFmpeg.AutoGen;
using static FFmpeg.AutoGen.ffmpeg;

using FlyleafLib.MediaFramework.MediaDemuxer;

namespace FlyleafLib.MediaFramework.MediaStream;

public unsafe class VideoStream : StreamBase
{
    public AspectRatio                  AspectRatio         { get; set; }
    public ColorRange                   ColorRange          { get; set; }
    public ColorSpace                   ColorSpace          { get; set; }
    public double                       FPS                 { get; set; }
    public long                         FrameDuration       { get ;set; }
    public int                          Height              { get; set; }
    public bool                         IsRGB               { get; set; }
    public AVComponentDescriptor[]      PixelComps          { get; set; }
    public int                          PixelComp0Depth     { get; set; }
    public AVPixelFormat                PixelFormat         { get; set; }
    public AVPixFmtDescriptor*          PixelFormatDesc     { get; set; }
    public string                       PixelFormatStr      { get; set; }
    public int                          PixelPlanes         { get; set; }
    public bool                         PixelSameDepth      { get; set; }
    public bool                         PixelInterleaved    { get; set; }
    public int                          TotalFrames         { get; set; }
    public int                          Width               { get; set; }

    public override string GetDump() { return $"[{Type} #{StreamIndex}] {Codec} {PixelFormatStr} {Width}x{Height} @ {FPS:#.###} | [Color: {ColorSpace}] [BR: {BitRate}] | {Utils.TicksToTime((long)(AVStream->start_time * Timebase))}/{Utils.TicksToTime((long)(AVStream->duration * Timebase))} | {Utils.TicksToTime(StartTime)}/{Utils.TicksToTime(Duration)}"; }
    public VideoStream() { }
    public VideoStream(Demuxer demuxer, AVStream* st) : base(demuxer, st)
    {
        Demuxer = demuxer;
        AVStream = st;
        Refresh();
    }

    public void Refresh(AVPixelFormat format = AVPixelFormat.AV_PIX_FMT_NONE)
    {
        base.Refresh();

        PixelFormat     = format == AVPixelFormat.AV_PIX_FMT_NONE ? (AVPixelFormat)AVStream->codecpar->format : format;
        PixelFormatStr  = PixelFormat.ToString().Replace("AV_PIX_FMT_","").ToLower();
        Width           = AVStream->codecpar->width;
        Height          = AVStream->codecpar->height;
        FPS             = av_q2d(AVStream->avg_frame_rate) > 0 ? av_q2d(AVStream->avg_frame_rate) : av_q2d(AVStream->r_frame_rate);
        FrameDuration   = FPS > 0 ? (long) (10000000 / FPS) : 0;
        TotalFrames     = AVStream->duration > 0 && FrameDuration > 0 ? (int) (AVStream->duration * Timebase / FrameDuration) : (FrameDuration > 0 ? (int) (Demuxer.Duration / FrameDuration) : 0);

        int gcd = Utils.GCD(Width, Height);
        if (gcd != 0)
            AspectRatio = new AspectRatio(Width / gcd , Height / gcd);

        if (PixelFormat != AVPixelFormat.AV_PIX_FMT_NONE)
        {
            ColorRange = AVStream->codecpar->color_range == AVColorRange.AVCOL_RANGE_JPEG ? ColorRange.Full : ColorRange.Limited;

            if (AVStream->codecpar->color_space == AVColorSpace.AVCOL_SPC_BT470BG)
                ColorSpace = ColorSpace.BT601;
            else if (AVStream->codecpar->color_space == AVColorSpace.AVCOL_SPC_BT709)
                ColorSpace = ColorSpace.BT709;
            else ColorSpace = AVStream->codecpar->color_space == AVColorSpace.AVCOL_SPC_BT2020_CL || AVStream->codecpar->color_space == AVColorSpace.AVCOL_SPC_BT2020_NCL
                ? ColorSpace.BT2020
                : Height > 576 ? ColorSpace.BT709 : ColorSpace.BT601;

            PixelFormatDesc = av_pix_fmt_desc_get(PixelFormat);
            var comps       = PixelFormatDesc->comp.ToArray();
            PixelComps      = new AVComponentDescriptor[PixelFormatDesc->nb_components];
            for (int i=0; i<PixelComps.Length; i++)
                PixelComps[i] = comps[i];

            PixelInterleaved= PixelFormatDesc->log2_chroma_w != PixelFormatDesc->log2_chroma_h;
            IsRGB           = (PixelFormatDesc->flags & AV_PIX_FMT_FLAG_RGB   ) != 0;

            PixelSameDepth  = true;
            PixelPlanes     = 0;
            if (PixelComps.Length > 0)
            {
                PixelComp0Depth = PixelComps[0].depth;
                int prevBit     = PixelComp0Depth;
                for (int i=0; i<PixelComps.Length; i++)
                {
                    if (PixelComps[i].plane > PixelPlanes)
                        PixelPlanes = PixelComps[i].plane;

                    if (prevBit != PixelComps[i].depth)
                        PixelSameDepth = false;
                }

                PixelPlanes++;
            }
        }
    }
}