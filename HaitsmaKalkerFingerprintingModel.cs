//
// AudioAlign: Audio Synchronization and Analysis Tool
// Copyright (C) 2010-2015  Mario Guggenberger <mg@protyposis.net>
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as
// published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.
//

using System.Diagnostics;
using Aurio.Matching;
using Aurio.Matching.HaitsmaKalker2002;
using Aurio.Project;
using Aurio.TaskMonitor;

namespace TrackWarp;

public class HaitsmaKalkerFingerprintingModel
{
    readonly Profile[] profiles;
    FingerprintStore store;

    public event EventHandler FingerprintingFinished;

    public HaitsmaKalkerFingerprintingModel()
    {
        FingerprintBerThreshold = 0.45f;
        FingerprintSize = FingerprintStore.DEFAULT_FINGERPRINT_SIZE;
        profiles = FingerprintGenerator.GetProfiles();
        SelectedProfile = profiles[0];
        Reset();
    }

    public Profile[] Profiles
    {
        get { return profiles; }
    }

    public Profile SelectedProfile { get; set; }

    public float FingerprintBerThreshold { get; set; }

    public int FingerprintSize { get; set; }

    /// <summary>
    /// Resets the model by clearing all data and configuring it with a new profile.
    /// </summary>
    /// <param name="profile">the new profile to configure the model with</param>
    public void Reset(Profile profile)
    {
        SelectedProfile = profile ?? throw new ArgumentNullException(nameof(profile));
        store = new FingerprintStore(profile);
    }

    /// <summary>
    /// Resets the model by clearing all data and configuring it with the current profile.
    /// </summary>
    public void Reset()
    {
        Reset(SelectedProfile);
    }

    public void Fingerprint(List<AudioTrack> tracks, ProgressMonitor progressMonitor)
    {
        HaitsmaKalkerFingerprintingModel selfReference = this;
        
        foreach (AudioTrack track in tracks)
        {
            IProgressReporter? progressReporter = progressMonitor.BeginTask(
                "Generating sub-fingerprints for " + track.FileInfo.Name,
                true
            );
            FingerprintGenerator generator = new FingerprintGenerator(SelectedProfile, track);
            int subFingerprintsCalculated = 0;

            generator.SubFingerprintsGenerated +=
                delegate(object _, SubFingerprintsGeneratedEventArgs e2)
                {
                    subFingerprintsCalculated += e2.SubFingerprints.Count;
                    progressReporter.ReportProgress(
                        (double)e2.Index / e2.Indices * 100
                    );
                    store.Add(e2);
                };

            generator.Generate();

            progressReporter.Finish();
            Debug.WriteLine(
                "subfingerprint generation finished with "
                + subFingerprintsCalculated
                + " hashes"
            );
        }

        FingerprintingFinished?.Invoke(selfReference, EventArgs.Empty);
    }

    public void FindAllMatches(Action<List<Match>> callback)
    {
        store.Threshold = FingerprintBerThreshold;
        store.FingerprintSize = FingerprintSize;
        
        List<Match> matches = store.FindAllMatches();
        
        matches = MatchProcessor.FilterDuplicateMatches(matches);
        Debug.WriteLine(matches.Count + " matches found (filtered)");

        callback.Invoke(matches);
    }
}