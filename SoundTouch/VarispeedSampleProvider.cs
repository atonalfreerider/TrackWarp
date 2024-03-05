using NAudio.Wave;

namespace VarispeedDemo.SoundTouch;

class VarispeedSampleProvider : ISampleProvider, IDisposable
{
    readonly ISampleProvider sourceProvider;
    readonly SoundTouch soundTouch;
    readonly float[] sourceReadBuffer;
    readonly float[] soundTouchReadBuffer;
    readonly int channelCount;
    float playbackRate = 1.0f;
    SoundTouchProfile? currentSoundTouchProfile;
    bool repositionRequested;

    public VarispeedSampleProvider(
        ISampleProvider sourceProvider, 
        int readDurationMilliseconds,
        SoundTouchProfile soundTouchProfile)
    {
        soundTouch = new SoundTouch();

        SetSoundTouchProfile(soundTouchProfile);
        this.sourceProvider = sourceProvider;
        soundTouch.SetSampleRate(WaveFormat.SampleRate);
        channelCount = WaveFormat.Channels;
        soundTouch.SetChannels(channelCount);
        sourceReadBuffer = new float[WaveFormat.SampleRate * channelCount * (long)readDurationMilliseconds / 1000];
        soundTouchReadBuffer = new float[sourceReadBuffer.Length * 10]; // support down to 0.1 speed
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (playbackRate <= float.Epsilon) // play silence
        {
            for (int n = 0; n < count; n++)
            {
                buffer[offset++] = 0;
            }

            return count;
        }

        if (repositionRequested)
        {
            soundTouch.Clear();
            repositionRequested = false;
        }

        int samplesRead = 0;
        bool reachedEndOfSource = false;
        while (samplesRead < count)
        {
            if (soundTouch.NumberOfSamplesAvailable == 0)
            {
                int readFromSource = sourceProvider.Read(sourceReadBuffer, 0, sourceReadBuffer.Length);
                if (readFromSource > 0)
                {
                    soundTouch.PutSamples(sourceReadBuffer, readFromSource / channelCount);
                }
                else
                {
                    reachedEndOfSource = true;
                    // we've reached the end, tell SoundTouch we're done
                    soundTouch.Flush();
                }
            }

            int desiredSampleFrames = (count - samplesRead) / channelCount;

            int received = soundTouch.ReceiveSamples(soundTouchReadBuffer, desiredSampleFrames) * channelCount;
            // use loop instead of Array.Copy due to WaveBuffer
            for (int n = 0; n < received; n++)
            {
                buffer[offset + samplesRead++] = soundTouchReadBuffer[n];
            }

            if (received == 0 && reachedEndOfSource) break;
        }

        return samplesRead;
    }

    public WaveFormat WaveFormat => sourceProvider.WaveFormat;

    public float PlaybackRate
    {
        get => playbackRate;
        set
        {
            if (Math.Abs(playbackRate - value) > float.Epsilon)
            {
                UpdatePlaybackRate(value);
                playbackRate = value;
            }
        }
    }

    void UpdatePlaybackRate(float value)
    {
        if (value != 0)
        {
            if (currentSoundTouchProfile != null && currentSoundTouchProfile.UseTempo)
            {
                soundTouch.SetTempo(value);
            }
            else
            {
                soundTouch.SetRate(value);
            }
        }
    }

    public void Dispose()
    {
        soundTouch.Dispose();
    }

    public void SetSoundTouchProfile(SoundTouchProfile soundTouchProfile)
    {
        if (currentSoundTouchProfile != null &&
            Math.Abs(playbackRate - 1.0f) > float.Epsilon &&
            soundTouchProfile.UseTempo != currentSoundTouchProfile.UseTempo)
        {
            if (soundTouchProfile.UseTempo)
            {
                soundTouch.SetRate(1.0f);
                soundTouch.SetPitchOctaves(0f);
                soundTouch.SetTempo(playbackRate);
            }
            else
            {
                soundTouch.SetTempo(1.0f);
                soundTouch.SetRate(playbackRate);
            }
        }

        currentSoundTouchProfile = soundTouchProfile;
        soundTouch.SetUseAntiAliasing(soundTouchProfile.UseAntiAliasing);
        soundTouch.SetUseQuickSeek(soundTouchProfile.UseQuickSeek);
    }

    public void Reposition()
    {
        repositionRequested = true;
    }
}