using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using Aurio;
using Aurio.FFmpeg;
using Aurio.FFT;
using Aurio.Matching;
using Aurio.Matching.HaitsmaKalker2002;
using Aurio.Project;
using Aurio.Resampler;
using Aurio.TaskMonitor;
using TrackWarp;

class Program
{
    static void Main(string[] args)
    {
        RootCommand rootCommand =
        [
            new Argument<string>("BaseAudioPath"),

            new Argument<string>("ConformAudioPath"),

            new Argument<string>("WarpAudioPath"),

            new Option<float>(["--fingerprint-ber-threshold", "-t"],
                getDefaultValue: () => FingerprintStore.DEFAULT_THRESHOLD,
                description: "The BER threshold for fingerprint matching. Defaults to 0.35.")
        ];

        rootCommand.Description = "Conform audio track to base track";

        rootCommand.Handler = CommandHandler.Create<string, string, string, float>(Parse);

        rootCommand.Invoke(args);

        Environment.Exit(0);
    }

    static void Parse(
        string baseAudioPath,
        string conformAudioPath,
        string warpAudioPath,
        float fingerprintBerThreshold)
    {
        // Use PFFFT as FFT implementation
        FFTFactory.Factory = new Aurio.PFFFT.FFTFactory();
        // Use Soxr as resampler implementation
        ResamplerFactory.Factory = new Aurio.Soxr.ResamplerFactory();
        // Use FFmpeg for file reading/decoding
        AudioStreamFactory.AddFactory(new FFmpegAudioStreamFactory());

        AudioTrack baseAudioTrack = new(new FileInfo(baseAudioPath));
        AudioTrack conformAudioTrack = new(new FileInfo(conformAudioPath));

        List<AudioTrack> audioTracks =
        [
            baseAudioTrack,
            conformAudioTrack
        ];

        HaitsmaKalkerFingerprintingModel model = new(fingerprintBerThreshold);
        model.FingerprintingFinished += delegate { model.FindAllMatches(TrackTimingCallback); };

        model.Reset();
        model.Fingerprint(audioTracks, new ProgressMonitor());
    }

    static void TrackTimingCallback(List<Match> matches)
    {
        if (matches.Count == 0)
        {
            Console.WriteLine("No matches found. Exiting.");
            return;
        }

        Dictionary<string, Dictionary<string, List<double>>> trackAndOffsetsToOtherTracks = [];

        foreach (Match match in matches)
        {
            string trackName = match.Track1.FileInfo.FullName;
            string otherTrackName = match.Track2.FileInfo.FullName;
            double offset = match.Offset.TotalSeconds;
            trackAndOffsetsToOtherTracks.TryGetValue(trackName, out Dictionary<string, List<double>>? otherTracks);
            if (otherTracks == null)
            {
                otherTracks = new Dictionary<string, List<double>>();
                trackAndOffsetsToOtherTracks.Add(trackName, otherTracks);
            }

            otherTracks.TryGetValue(otherTrackName, out List<double>? offsets);
            if (offsets == null)
            {
                offsets = new List<double>();
                otherTracks.Add(otherTrackName, offsets);
            }

            offsets.Add(offset);
        }

        // get the median offset, incrementing from the first entry to the last
        KeyValuePair<string, Dictionary<string, List<double>>> maxKeyValuePair =
            trackAndOffsetsToOtherTracks.MaxBy(kv =>
                kv.Value.Count); // Take the first item after ordering, which will have the maximum count

        Dictionary<string, double> trackOffsets = new() { { maxKeyValuePair.Key, 0 } };

        foreach ((string other, List<double> offsets) in maxKeyValuePair.Value)
        {
            offsets.Sort();
            double medianOffset = offsets[offsets.Count / 2];
            trackOffsets.Add(other, medianOffset);
        }

        foreach (KeyValuePair<string, double> keyValuePair in trackOffsets)
        {
            Console.WriteLine(keyValuePair.Key + " " + keyValuePair.Value);
        }
    }
}