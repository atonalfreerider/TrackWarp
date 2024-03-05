using NAudio.Wave;

namespace TrackWarp;

public class Mp3SampleProvider : IDisposable
{
    private readonly string _filePath;
    private Mp3FileReader _mp3Reader;
    private ISampleProvider _sampleProvider;

    public Mp3SampleProvider(string filePath)
    {
        _filePath = filePath;
        Initialize();
    }

    private void Initialize()
    {
        _mp3Reader = new Mp3FileReader(_filePath);
        _sampleProvider = _mp3Reader.ToSampleProvider();
    }

    public ISampleProvider GetSampleProvider()
    {
        return _sampleProvider;
    }

    public void Dispose()
    {
        _mp3Reader?.Dispose();
    }
}