using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using Aurio;
using Aurio.FFmpeg;
using Aurio.FFT;
using Aurio.Matching;
using Aurio.Matching.Dixon2005;
using Aurio.Matching.HaitsmaKalker2002;
using Aurio.Project;
using Aurio.Resampler;
using Aurio.Streams;
using Aurio.TaskMonitor;

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
            100
        );

        if (matches.Count == 0)
        {
            Console.WriteLine("No matches found. Exiting.");
            return;
        }

        foreach (Match match in matches)
        {
            Console.WriteLine(match.Track1Time.ToString(@"hh\:mm\:ss\.fff") + " -> " +
                              match.Track2Time.ToString(@"hh\:mm\:ss\.fff"));
        }
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
        int filterSize
    )
    {
        TimeSpan TimeWarpSearchWidth = TimeSpan.FromSeconds(searchWidth);
        ProgressMonitor progressMonitor = new ProgressMonitor();

        int TimeWarpFilterSize = filterSize;
        const bool TimeWarpSmoothing = true;
        const bool TimeWarpInOutCue = true;

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

        List<Tuple<TimeSpan, TimeSpan>> path = null;

        DTW? dtw = type switch
        {
            // execute time warping
            TimeWarpType.DTW => new DTW(TimeWarpSearchWidth, progressMonitor),
            TimeWarpType.OLTW => new OLTW2(TimeWarpSearchWidth, progressMonitor),
            _ => null
        };

        if (dtw == null)
        {
            return [];
        }

        path = dtw.Execute(s1, s2);

        if (path == null)
        {
            return [];
        }

        // convert resulting path to matches and filter them
        int smoothingWidth = Math.Max(1, Math.Min(TimeWarpFilterSize / 10, TimeWarpFilterSize));
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
        if (TimeWarpInOutCue)
        {
            int startIndex = 0;
            int endIndex = path.Count;

            // this needs a temporally ordered mapping path (no matter if ascending or descending)
            foreach (Tuple<TimeSpan, TimeSpan> mapping in path)
            {
                if (cueIn && (mapping.Item1 < TimeWarpSearchWidth || mapping.Item2 < TimeWarpSearchWidth))
                {
                    startIndex++;
                }

                if (
                    cueOut
                    && (
                        mapping.Item1 > (t1To - t1From - TimeWarpSearchWidth)
                        || mapping.Item2 > (t2To - t2From - TimeWarpSearchWidth)
                    )
                )
                {
                    endIndex--;
                }
            }

            path = path.GetRange(startIndex, endIndex - startIndex);
        }

        for (int i = 0; i < path.Count; i += TimeWarpFilterSize)
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

            if (smoothingSection.Count == 1 || !TimeWarpSmoothing || i == 0)
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
                new Match()
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
            progress += TimeWarpFilterSize;
        }

        // add last match if it hasn't been added
        if (path.Count > 0 && path.Count % TimeWarpFilterSize != 1)
        {
            Tuple<TimeSpan, TimeSpan> lastMatch = path[^1];
            matches.Add(
                new Match()
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
}