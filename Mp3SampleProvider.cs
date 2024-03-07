using NAudio.Wave;

namespace TrackWarp;

public class Mp3SampleProvider : IDisposable
{
    readonly string filePath;
    Mp3FileReader mp3Reader;
    ISampleProvider sampleProvider;

    public Mp3SampleProvider(string filePath)
    {
        this.filePath = filePath;
        Initialize();
    }

    void Initialize()
    {
        mp3Reader = new Mp3FileReader(filePath);
        sampleProvider = mp3Reader.ToSampleProvider();
    }

    public ISampleProvider GetSampleProvider()
    {
        return sampleProvider;
    }

    public void Dispose()
    {
        mp3Reader?.Dispose();
    }
}