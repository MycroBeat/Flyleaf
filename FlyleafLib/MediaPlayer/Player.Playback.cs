﻿using FlyleafLib.MediaFramework.MediaDecoder;

namespace FlyleafLib.MediaPlayer;

partial class Player
{
    string stoppedWithError = null;

    /// <summary>
    /// Fires on playback stopped by an error or completed / ended successfully <see cref="Status"/>
    /// Warning: Uses Invoke and it comes from playback thread so you can't pause/stop etc. You need to use another thread if you have to.
    /// </summary>
    public event EventHandler<PlaybackStoppedArgs> PlaybackStopped;
    protected virtual void OnPlaybackStopped(string error = null)
    {
        if (error != null && lastError == null)
        {
            lastError = error;
            UI(() => LastError = lastError);
        }

        PlaybackStopped?.Invoke(this, new PlaybackStoppedArgs(error));
    }

    /// <summary>
    /// Fires on seek completed for the specified ms (ms will be -1 on failure)
    /// </summary>
    public event EventHandler<int> SeekCompleted;

    /// <summary>
    /// Plays AVS streams
    /// </summary>
    public void Play()
    {
        lock (lockActions)
        {
            if (!canPlay || status == Status.Playing || status == Status.Ended)
                return;

            status = Status.Playing;
            UI(() => Status = status);
        }

        while (taskPlayRuns || taskSeekRuns) Thread.Sleep(5);
        taskPlayRuns = true;

        Thread t = new(PlayThread)
        {
            #if DEBUG
            Name            = $"[#{PlayerId}] Playback",
            #endif
            Priority        = Config.Player.ThreadPriority,
            IsBackground    = true
        };

        t.Start();
    }

    void PlayThread()
    {
        try
        {
            Engine.TimeBeginPeriod1();
            Engine.ThreadExecutionStateBegin();

            onBufferingStarted   = 0;
            onBufferingCompleted = 0;
            requiresBuffering    = true;

            if (lastError != null)
            {
                lastError = null;
                UI(() => LastError = lastError);
            }

            if (Config.Player.Usage == Usage.Audio || !Video.IsOpened)
                ScreamerAudioOnly();
            else
            {
                if (Config.Player.ZeroLatency)
                    ScreamerZeroLatency();
                else if (ReversePlayback)
                {
                    shouldFlushNext = true;
                    ScreamerReverse();
                }
                else
                {
                    shouldFlushPrev = true;
                    ScreamerVASD();
                }
            }

        }
        catch (Exception e)
        {
            Log.Error($"Playback failed ({e.Message})");
        }
        finally
        {
            if (vFrame != renderer.LastFrame)
                VideoDecoder.DisposeFrame(vFrame);
            vFrame = null;
            sFrame = null;

            if (status == Status.Stopped)
                decoder?.Initialize();
            else if (decoder != null)
            {
                decoder.PauseOnQueueFull();
                decoder.PauseDecoders();
            }

            Audio.ClearBuffer();
            Engine.TimeEndPeriod1();
            Engine.ThreadExecutionStateEnd();
            stoppedWithError = null;

            if (status == Status.Playing)
            {
                if (decoderHasEnded)
                    status = Status.Ended;
                else
                {
                    if (Video.IsOpened && VideoDemuxer.Interrupter.Timedout)
                        stoppedWithError = "Timeout";
                    else if (onBufferingStarted - 1 == onBufferingCompleted)
                    {
                        stoppedWithError = "Playback stopped unexpectedly";
                        OnBufferingCompleted("Buffering failed");
                    }
                    else
                    {
                        if (!ReversePlayback)
                        {
                            if (isLive || Math.Abs(duration - curTime) > 3 * 1000 * 10000)
                                stoppedWithError = "Playback stopped unexpectedly";
                        }
                        else if (curTime > 3 * 1000 * 10000)
                            stoppedWithError = "Playback stopped unexpectedly";
                    }

                    status = Status.Paused;
                }
            }

            OnPlaybackStopped(stoppedWithError);
            if (CanDebug) Log.Debug($"[SCREAMER] Finished (Status: {status}, Error: {stoppedWithError})");

            UI(() =>
            {
                Status = status;
                SetCurTime();
            });

            taskPlayRuns = false;
        }
    }

    /// <summary>
    /// Pauses AVS streams
    /// </summary>
    public void Pause()
    {
        lock (lockActions)
        {
            if (!canPlay || status == Status.Ended)
                return;

            status = Status.Paused;
            UI(() => Status = status);

            while (taskPlayRuns) Thread.Sleep(5);
        }
    }

    public void TogglePlayPause()
    {
        if (status == Status.Playing)
            Pause();
        else
            Play();
    }

    public void ToggleReversePlayback()
        => ReversePlayback = !ReversePlayback;

    public void ToggleLoopPlayback()
        => LoopPlayback = !LoopPlayback;

    /// <summary>
    /// Seeks backwards or forwards based on the specified ms to the nearest keyframe
    /// </summary>
    /// <param name="ms"></param>
    /// <param name="forward"></param>
    public void Seek(int ms, bool forward = false) => Seek(ms, forward, false);

    /// <summary>
    /// Seeks at the exact timestamp (with half frame distance accuracy)
    /// </summary>
    /// <param name="ms"></param>
    public void SeekAccurate(int ms)
        => Seek(ms, false, !IsLive);

    public void ToggleSeekAccurate()
        => Config.Player.SeekAccurate = !Config.Player.SeekAccurate;

    private void Seek(int ms, bool forward, bool accurate)
    {
        if (!canPlay)
            return;

        lock (seeks)
        {
            _CurTime = curTime = ms * (long)10000;
            seeks.Push(new SeekData(ms, forward, accurate));
        }

        // TBR: We consider UI here? (_CurTime loses sync)
        // BufferedDuration = 0; Can't se this as it reads the demuxer's (which we didn't seek yet..)
        Raise(nameof(CurTime));

        if (status == Status.Playing)
            return;

        lock (seeks)
        {
            if (taskSeekRuns)
                return;

            taskSeekRuns = true;
        }

        Task.Run(() =>
        {
            int ret;
            bool wasEnded = false;
            SeekData seekData = null;

            try
            {
                Engine.TimeBeginPeriod1();

                while (true)
                {
                    lock (seeks)
                    {
                        if (!(seeks.TryPop(out seekData) && canPlay && status != Status.Playing))
                        {
                            taskSeekRuns = false;
                            break;
                        }

                        seeks.Clear();
                    }

                    if (status == Status.Ended)
                    {
                        wasEnded = true;
                        status = Status.Paused;
                        UI(() => Status = status);
                    }

                    if (sFramePrev != null)
                    {
                        sFramePrev = null;
                        renderer.ClearOverlayTexture();
                        Subtitles.ClearSubsText();
                    }

                    if (!Video.IsOpened)
                    {
                        if (AudioDecoder.OnVideoDemuxer)
                        {
                            ret = decoder.Seek(seekData.ms, seekData.forward);
                            if (CanWarn && ret < 0)
                                Log.Warn("Seek failed 2");

                            VideoDemuxer.Start();
                            SeekCompleted?.Invoke(this, -1);
                        }
                        else
                        {
                            ret = decoder.SeekAudio(seekData.ms, seekData.forward);
                            if (CanWarn && ret < 0)
                                Log.Warn("Seek failed 3");

                            AudioDemuxer.Start();
                            SeekCompleted?.Invoke(this, -1);
                        }

                        decoder.PauseOnQueueFull();
                        SeekCompleted?.Invoke(this, seekData.ms);
                    }
                    else
                    {
                        decoder.PauseDecoders();
                        ret = decoder.Seek(seekData.accurate ? Math.Max(0, seekData.ms - 3000) : seekData.ms, seekData.forward, !seekData.accurate); // 3sec ffmpeg bug for seek accurate when fails to seek backwards (see videodecoder getframe)
                        if (ret < 0)
                        {
                            if (CanWarn) Log.Warn("Seek failed");
                            SeekCompleted?.Invoke(this, -1);
                        }
                        else if (!ReversePlayback && CanPlay)
                        {
                            decoder.GetVideoFrame(seekData.accurate ? seekData.ms * (long)10000 : -1);
                            ResetFrameStats();
                            ShowOneFrame();
                            VideoDemuxer.Start();
                            AudioDemuxer.Start();
                            SubtitlesDemuxer.Start();
                            DataDemuxer.Start();
                            decoder.PauseOnQueueFull();
                            SeekCompleted?.Invoke(this, seekData.ms);
                        }
                    }

                    Thread.Sleep(20);
                }
            }
            catch (Exception e)
            {
                lock (seeks) taskSeekRuns = false;
                Log.Error($"Seek failed ({e.Message})");
            }
            finally
            {
                decoder.OpenedPlugin?.OnBufferingCompleted();
                Engine.TimeEndPeriod1();
                if ((wasEnded && Config.Player.AutoPlay) || stoppedWithError != null) // TBR: Possible race condition with if (Status == Status.Playing)
                    Play();
            }
        });
    }

    /// <summary>
    /// Flushes the buffer (demuxers (packets) and decoders (frames))
    /// This is useful mainly for live streams to push the playback at very end (low latency)
    /// </summary>
    public void Flush()
        => decoder.Flush();

    /// <summary>
    /// Stops and Closes AVS streams
    /// </summary>
    public void Stop()
    {
        lock (lockActions)
        {
            Initialize();
            renderer?.Flush();
        }
    }
}

public class PlaybackStoppedArgs : EventArgs
{
    public string   Error       { get; }
    public bool     Success     { get; }

    public PlaybackStoppedArgs(string error)
    {
        Error   = error;
        Success = Error == null;
    }
}

class SeekData
{
    public int  ms;
    public bool forward;
    public bool accurate;
    public SeekData(int ms, bool forward, bool accurate)
        { this.ms = ms; this.forward = forward && !accurate; this.accurate = accurate; }
}
