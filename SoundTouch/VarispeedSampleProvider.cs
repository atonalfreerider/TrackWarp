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
            Array.Clear(buffer, offset, count); // More efficient way to set silence
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
            if (soundTouch.NumberOfSamplesAvailable == 0 && !reachedEndOfSource)
            {
                // Read from the source into the temporary buffer
                int readFromSource = sourceProvider.Read(sourceReadBuffer, 0, sourceReadBuffer.Length);

                if (readFromSource > 0)
                {
                    // We have successfully read from source, put samples into SoundTouch
                    soundTouch.PutSamples(sourceReadBuffer, readFromSource / channelCount);
                }
                else
                {
                    // No more samples are available from the source, indicate we've reached the end
                    Console.WriteLine("Reached end of source, indicating to SoundTouch.");
                    reachedEndOfSource = true;
                    soundTouch.Flush(); // Indicate to SoundTouch that no more samples will be put
                }
            }

            // Calculate the number of sample frames we still need to process
            int desiredSampleFrames = (count - samplesRead) / channelCount;

            // Try to receive processed samples from SoundTouch
            int received = soundTouch.ReceiveSamples(soundTouchReadBuffer, desiredSampleFrames) * channelCount;

            // Copy the received samples to the output buffer
            for (int n = 0; n < received; n++)
            {
                buffer[offset + samplesRead++] = soundTouchReadBuffer[n];
            }

            // Break out of the loop if we received no new samples and we've reached the end of the source
            if (received == 0 && reachedEndOfSource) 
            {
                Console.WriteLine("Received 0 samples and reached end of source, breaking out of loop.");
                return 0;
            }
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