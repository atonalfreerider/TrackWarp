using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using Aurio;
using Aurio.FFmpeg;
using Aurio.FFT;
using Aurio.Matching;
using Aurio.Matching.Dixon2005;
using Aurio.Project;
using Aurio.Resampler;
using Aurio.Streams;
using Aurio.TaskMonitor;
using NAudio.Lame;
using NAudio.Wave;
using VarispeedDemo.SoundTouch;

namespace TrackWarp;

class Program
{
    static void Main(string[] args)
    {
        RootCommand rootCommand =
        [
            new Argument<string>("BaseAudioPath",
                "Path to the audio file that will serve as the base mapping for the warped audio."),

            new Argument<string>("ComparisonAudioPath",
                "Path to the audio file that will provide the mapping deltas to the base audio."),

            new Argument<string>("WarpAudioPath",
                "Path to the audio file that will be warped to match the base audio, using the mappings."),

            new Argument<string>("OutputAudioPath",
                "The final output wav file path.")
        ];

        rootCommand.Description = "Conform audio track to base track";

        rootCommand.Handler = CommandHandler.Create<string, string, string, string>(Parse);

        rootCommand.Invoke(args);

        Environment.Exit(0);
    }

    static void Parse(
        string baseAudioPath,
        string comparisonAudioPath,
        string warpAudioPath,
        string outputAudioPath)
    {
        // Use PFFFT as FFT implementation
        FFTFactory.Factory = new Aurio.PFFFT.FFTFactory();
        // Use Soxr as resampler implementation
        ResamplerFactory.Factory = new Aurio.Soxr.ResamplerFactory();
        // Use FFmpeg for file reading/decoding
        AudioStreamFactory.AddFactory(new FFmpegAudioStreamFactory());

        AudioTrack baseAudioTrack = new(new FileInfo(baseAudioPath));
        AudioTrack conformAudioTrack = new(new FileInfo(comparisonAudioPath));

        List<Match> matches = TimeWarp(
            TimeWarpType.DTW,
            baseAudioTrack,
            TimeSpan.Zero,
            baseAudioTrack.Length,
            conformAudioTrack,
            TimeSpan.Zero,
            conformAudioTrack.Length,
            true,
            false,
            false,
            30,
            100,
            true,
            true
        );

        if (matches.Count == 0)
        {
            Console.WriteLine("No matches found. Exiting.");
            return;
        }

        WriteToWav(outputAudioPath, matches, warpAudioPath);
        ConvertToMp3(outputAudioPath);

        string waveOutPath = Path.ChangeExtension(outputAudioPath, ".wav");
        File.Delete(waveOutPath);

        Console.WriteLine("Wrote warped audio to " + outputAudioPath);
    }

    static List<Match> TimeWarp(
        TimeWarpType type,
        AudioTrack t1,
        TimeSpan t1From,
        TimeSpan t1To,
        AudioTrack t2,
        TimeSpan t2From,
        TimeSpan t2To,
        bool calculateSimilarity,
        bool cueIn,
        bool cueOut,
        int searchWidth,
        int filterSize,
        bool timeWarpInOutCue,
        bool timeWarpSmoothing)
    {
        TimeSpan timeWarpSearchWidth = TimeSpan.FromSeconds(searchWidth);
        ProgressMonitor progressMonitor = new();

        IAudioStream s1 = t1.CreateAudioStream();
        IAudioStream s2 = t2.CreateAudioStream();
        s1 = new CropStream(
            s1,
            TimeUtil.TimeSpanToBytes(t1From, s1.Properties),
            TimeUtil.TimeSpanToBytes(t1To, s1.Properties)
        );
        s2 = new CropStream(
            s2,
            TimeUtil.TimeSpanToBytes(t2From, s2.Properties),
            TimeUtil.TimeSpanToBytes(t2To, s2.Properties)
        );

        DTW? dtw = type switch
        {
            // execute time warping
            TimeWarpType.DTW => new DTW(timeWarpSearchWidth, progressMonitor),
            TimeWarpType.OLTW => new OLTW2(timeWarpSearchWidth, progressMonitor),
            _ => null
        };

        if (dtw == null)
        {
            return [];
        }

        List<Tuple<TimeSpan, TimeSpan>>? path = dtw.Execute(s1, s2);

        if (path == null)
        {
            return [];
        }

        // convert resulting path to matches and filter them
        int smoothingWidth = Math.Max(1, Math.Min(filterSize / 10, filterSize));
        List<Match> matches = [];
        float maxSimilarity = 0; // needed for normalization
        IProgressReporter progressReporter = progressMonitor.BeginTask(
            "post-process resulting path...",
            true
        );
        double totalProgress = path.Count;
        double progress = 0;

        /* Leave out matches in the in/out cue areas...
         * The matches in the interval at the beginning and end of the calculated time warping path with a width
         * equal to the search width should be left out because they might not be correct - since the time warp
         * path needs to start at (0,0) in the matrix and end at (m,n), they would only be correct if the path gets
         * calculated between two synchronization points. Paths calculated from the start of a track to the first
         * sync point, or from the last sync point to end of the track are probably wrong in this interval since
         * the start and end points don't match if there is time drift so it is better to leave them out in those
         * areas... in those short a few second long intervals the drict actually will never be that extreme that
         * someone would notice it anyway. */
        if (timeWarpInOutCue)
        {
            int startIndex = 0;
            int endIndex = path.Count;

            // this needs a temporally ordered mapping path (no matter if ascending or descending)
            foreach (Tuple<TimeSpan, TimeSpan> mapping in path)
            {
                if (cueIn && (mapping.Item1 < timeWarpSearchWidth || mapping.Item2 < timeWarpSearchWidth))
                {
                    startIndex++;
                }

                if (
                    cueOut
                    && (
                        mapping.Item1 > (t1To - t1From - timeWarpSearchWidth)
                        || mapping.Item2 > (t2To - t2From - timeWarpSearchWidth)
                    )
                )
                {
                    endIndex--;
                }
            }

            path = path.GetRange(startIndex, endIndex - startIndex);
        }

        for (int i = 0; i < path.Count; i += filterSize)
        {
            //List<Tuple<TimeSpan, TimeSpan>> section = path.GetRange(i, Math.Min(path.Count - i, filterSize));
            List<Tuple<TimeSpan, TimeSpan>> smoothingSection = path.GetRange(
                Math.Max(0, i - smoothingWidth / 2),
                Math.Min(path.Count - i, smoothingWidth)
            );
            Tuple<TimeSpan, TimeSpan> match = path[i];

            if (smoothingSection.Count == 0)
            {
                throw new InvalidOperationException("must not happen");
            }

            if (smoothingSection.Count == 1 || !timeWarpSmoothing || i == 0)
            {
                // do nothing, match doesn't need any processing
                // the first and last match must not be smoothed since they must sit at the bounds
            }
            else
            {
                List<TimeSpan> offsets =
                    [..smoothingSection.Select(t => t.Item2 - t.Item1).OrderBy(t => t)];
                int middle = offsets.Count / 2;

                // calculate median
                // http://en.wikiversity.org/wiki/Primary_mathematics/Average,_median,_and_mode#Median
                TimeSpan smoothedDriftTime =
                    new((offsets[middle - 1] + offsets[middle]).Ticks / 2);
                match = new Tuple<TimeSpan, TimeSpan>(
                    match.Item1,
                    match.Item1 + smoothedDriftTime
                );
            }

            float similarity = calculateSimilarity
                ? (float)
                Math.Abs(
                    CrossCorrelation.Correlate(
                        s1,
                        new Interval(
                            match.Item1.Ticks,
                            match.Item1.Ticks + TimeUtil.SECS_TO_TICKS
                        ),
                        s2,
                        new Interval(
                            match.Item2.Ticks,
                            match.Item2.Ticks + TimeUtil.SECS_TO_TICKS
                        )
                    )
                )
                : 1;

            if (similarity > maxSimilarity)
            {
                maxSimilarity = similarity;
            }

            matches.Add(
                new Match
                {
                    Track1 = t1,
                    Track1Time = match.Item1 + t1From,
                    Track2 = t2,
                    Track2Time = match.Item2 + t2From,
                    Similarity = similarity,
                    Source = type.ToString()
                }
            );

            progressReporter.ReportProgress(progress / totalProgress * 100);
            progress += filterSize;
        }

        // add last match if it hasn't been added
        if (path.Count > 0 && path.Count % filterSize != 1)
        {
            Tuple<TimeSpan, TimeSpan> lastMatch = path[^1];
            matches.Add(
                new Match
                {
                    Track1 = t1,
                    Track1Time = lastMatch.Item1 + t1From,
                    Track2 = t2,
                    Track2Time = lastMatch.Item2 + t2From,
                    Similarity = 1,
                    Source = type.ToString()
                }
            );
        }

        progressReporter.Finish();

        s1.Close();
        s2.Close();

        return matches;
    }

    static void WriteToWav(string outputAudioPath, IReadOnlyList<Match> matches, string warpAudioPath)
    {
        using Mp3SampleProvider provider = new(warpAudioPath);

        ISampleProvider warpSampleProvider = provider.GetSampleProvider();

        // Prepare the audio for time stretching
        VarispeedSampleProvider stretchProvider = new(
            warpSampleProvider,
            100,
            new SoundTouchProfile(true, true));

        // Create a new WaveFileWriter to output the warped audio
        string waveOutPath = Path.ChangeExtension(outputAudioPath, ".wav");
        using WaveFileWriter writer = new(waveOutPath, stretchProvider.WaveFormat);
        for (int i = 0; i < matches.Count - 1; i++)
        {
            Match match = matches[i];
            Match nextMatch = matches[i + 1];

            double runTime = nextMatch.Track2Time.TotalSeconds - match.Track2Time.TotalSeconds;

            // Calculate the time stretch factor required to align this peak with the corresponding peak in the base track
            double timeDifference = nextMatch.Offset.TotalSeconds;
            if (Math.Abs(timeDifference) > float.Epsilon)
            {
                // Calculate the new stretch factor, ensuring no division by zero
                double stretchFactor = (nextMatch.Track2Time.TotalSeconds + timeDifference) /
                                       nextMatch.Track2Time.TotalSeconds;

                Console.WriteLine("Adjusting playback rate to " + stretchFactor);
                stretchProvider.PlaybackRate = (float)stretchFactor;
            }

            // Read samples from the stretch provider and write them to the output file
            float[] buffer =
                new float[(int)Math.Round(writer.WaveFormat.SampleRate * writer.WaveFormat.Channels * runTime)];
            int samplesRead = stretchProvider.Read(buffer, 0, buffer.Length);

            if (samplesRead == 0) continue;
            writer.WriteSamples(buffer, 0, samplesRead);
        }
    }

    static void ConvertToMp3(string outputAudioPath)
    {
        string mp3FileName = Path.ChangeExtension(outputAudioPath, ".mp3");
        string waveOutPath = Path.ChangeExtension(outputAudioPath, ".wav");

        using WaveFileReader reader = new(waveOutPath);
        using LameMP3FileWriter mp3Writer = new(mp3FileName, reader.WaveFormat, LAMEPreset.STANDARD);
        reader.CopyTo(mp3Writer);
    }
}