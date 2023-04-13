﻿using System;
using System.Collections.Concurrent;
using System.Threading;

using FFmpeg.AutoGen;
using static FFmpeg.AutoGen.ffmpeg;

using FlyleafLib.MediaFramework.MediaStream;
using FlyleafLib.MediaFramework.MediaFrame;

using static FlyleafLib.Logger;

namespace FlyleafLib.MediaFramework.MediaDecoder;

public unsafe class SubtitlesDecoder : DecoderBase
{
    public SubtitlesStream  SubtitlesStream     => (SubtitlesStream) Stream;

    public ConcurrentQueue<SubtitlesFrame>
                            Frames              { get; protected set; } = new ConcurrentQueue<SubtitlesFrame>();

    public SubtitlesDecoder(Config config, int uniqueId = -1) : base(config, uniqueId) { }

    protected override unsafe int Setup(AVCodec* codec) => 0;

    protected override void DisposeInternal()
        => Frames = new ConcurrentQueue<SubtitlesFrame>();

    public void Flush()
    {
        lock (lockActions)
        lock (lockCodecCtx)
        {
            if (Disposed) return;

            if (Status == Status.Ended) Status = Status.Stopped;
            //else if (Status == Status.Draining) Status = Status.Stopping;

            DisposeFrames();
            avcodec_flush_buffers(codecCtx);
        }
    }

    protected override void RunInternal()
    {
        int ret = 0;
        int allowedErrors = Config.Decoder.MaxErrors;
        AVPacket *packet;

        do
        {
            // Wait until Queue not Full or Stopped
            if (Frames.Count >= Config.Decoder.MaxSubsFrames)
            {
                lock (lockStatus)
                    if (Status == Status.Running) Status = Status.QueueFull;

                while (Frames.Count >= Config.Decoder.MaxSubsFrames && Status == Status.QueueFull) Thread.Sleep(20);

                lock (lockStatus)
                {
                    if (Status != Status.QueueFull) break;
                    Status = Status.Running;
                }       
            }

            // While Packets Queue Empty (Ended | Quit if Demuxer stopped | Wait until we get packets)
            if (demuxer.SubtitlesPackets.Count == 0)
            {
                CriticalArea = true;

                lock (lockStatus)
                    if (Status == Status.Running) Status = Status.QueueEmpty;

                while (demuxer.SubtitlesPackets.Count == 0 && Status == Status.QueueEmpty)
                {
                    if (demuxer.Status == Status.Ended)
                    {
                        Status = Status.Ended;
                        break;
                    }
                    else if (!demuxer.IsRunning)
                    {
                        if (CanDebug) Log.Debug($"Demuxer is not running [Demuxer Status: {demuxer.Status}]");

                        int retries = 5;

                        while (retries > 0)
                        {
                            retries--;
                            Thread.Sleep(10);
                            if (demuxer.IsRunning) break;
                        }

                        lock (demuxer.lockStatus)
                        lock (lockStatus)
                        {
                            if (demuxer.Status == Status.Pausing || demuxer.Status == Status.Paused)
                                Status = Status.Pausing;
                            else if (demuxer.Status != Status.Ended)
                                Status = Status.Stopping;
                            else
                                continue;
                        }

                        break;
                    }
                    
                    Thread.Sleep(20);
                }

                lock (lockStatus)
                {
                    CriticalArea = false;
                    if (Status != Status.QueueEmpty) break;
                    Status = Status.Running;
                }
            }
            
            lock (lockCodecCtx)
            {
                if (Status == Status.Stopped || demuxer.SubtitlesPackets.Count == 0) continue;
                packet = demuxer.SubtitlesPackets.Dequeue();
                int gotFrame = 0;
                AVSubtitle sub = new();
                ret = avcodec_decode_subtitle2(codecCtx, &sub, &gotFrame, packet);
                if (ret < 0)
                {
                    allowedErrors--;
                    if (CanWarn) Log.Warn($"{FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

                    if (allowedErrors == 0) { Log.Error("Too many errors!"); Status = Status.Stopping; break; }

                    continue;
                }
                        
                if (gotFrame < 1 || sub.num_rects < 1 ) continue;
                if (packet->pts == AV_NOPTS_VALUE) { avsubtitle_free(&sub); av_packet_free(&packet); continue; }

                var mFrame = ProcessSubtitlesFrame(packet, &sub);
                if (mFrame != null) Frames.Enqueue(mFrame);

                avsubtitle_free(&sub);
                av_packet_free(&packet);
            }
        } while (Status == Status.Running);
    }

    private SubtitlesFrame ProcessSubtitlesFrame(AVPacket* packet, AVSubtitle* sub)
    {

        try
        {
            string  line    = "";
            byte[]  buffer;
            var     rects   = sub->rects;
            var     cur     = rects[0];
            
            switch (cur->type)
            {
                case AVSubtitleType.SUBTITLE_ASS:
                case AVSubtitleType.SUBTITLE_TEXT:
                    buffer = new byte[1024];
                    line = Utils.BytePtrToStringUTF8(cur->ass);
                    break;

                //case AVSubtitleType.SUBTITLE_BITMAP:
                    //Log("Subtitles BITMAP -> Not Implemented yet");

                default:
                    return null;
            }

            SubtitlesFrame mFrame = new(line)
            {
                duration    = (int)(sub->end_display_time - sub->start_display_time),
                timestamp   = (long)(packet->pts * SubtitlesStream.Timebase) - demuxer.StartTime + Config.Subtitles.Delay
            };

            if (CanTrace) Log.Trace($"Processes {Utils.TicksToTime(mFrame.timestamp)}");

            Config.Subtitles.Parser(mFrame);

            return mFrame;
        } catch (Exception e) { Log.Error($"Failed to process frame ({e.Message})"); return null; }
    }

    public void DisposeFrames()
        => Frames = new ConcurrentQueue<SubtitlesFrame>();
}