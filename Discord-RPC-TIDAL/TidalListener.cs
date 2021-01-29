﻿using Squalr.Engine.DataTypes;
using Squalr.Engine.Memory;
using Squalr.Engine.OS;
using Squalr.Engine.Scanning.Scanners;
using Squalr.Engine.Scanning.Scanners.Constraints;
using Squalr.Engine.Scanning.Snapshots;
using Squalr.Engine.Snapshots;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace discord_rpc_tidal
{
    class TidalListener : IDisposable
    {
        #region Constants
        private const string PROCESSNAME = "TIDAL";
        private const string SPLITSTRING = "-";
        private const int REFRESHINTERVAL = 1000;
        private const int REFRESHINTERVALADDRESS = 2000;
        #endregion


        #region Properties
        public string CurrentSong { get; private set; }

        public double? CurrentTimecode { get; private set; }

        public bool ScanActive { get; private set; }

        public Process Process { get; private set; }
        #endregion


        #region Events
        public delegate void SongChangedEventHandler(string oldSong, string newSong);
        public event SongChangedEventHandler SongChanged;

        public delegate void TimecodeChangedEventHandler(double? oldTimecode, double? newTimeCode);
        public event TimecodeChangedEventHandler TimecodeChanged;
        #endregion


        public TidalListener()
        {
            UpdateSongInfoTimer.Elapsed += (object sender, ElapsedEventArgs e) => UpdateSongInfo();
        }


        /// <returns>(title, artist) of the currently playing song or ("", "") if unknown</returns>
        public (string, string) GetSongAndArtist()
        {
            var cut = CurrentSong.Split(SPLITSTRING, 2, StringSplitOptions.TrimEntries);

            return cut.Length switch
            {
                1 => (cut[0], string.Empty),
                2 => (cut[0], cut[1]),
                _ => (string.Empty, string.Empty)
            };
        }

        private void UpdateProcess()
        {
            foreach (var process in Process.GetProcessesByName(PROCESSNAME))
            {
                if (!string.IsNullOrWhiteSpace(process.MainWindowTitle))
                {
                    if (Process == null || Process.Id != process.Id)
                    {
                        TimecodeAddress = null;
                        MostRecentSong = null;
                    }

                    Process = process;
                }

                return;
            }

            Process = null;
        }

        private string MostRecentSong;
        private void UpdateSongInfo()
        {
            UpdateProcess();

            Stopwatch songStartTime = new Stopwatch();
            songStartTime.Start();

            // update song
            var oldSong = CurrentSong;
            var oldMostRecentSong = MostRecentSong;
            if (Process == null || Process.MainWindowTitle.Trim() == PROCESSNAME) // if no song is playing
            {
                CurrentSong = null;
            }
            else
            {
                CurrentSong = Process.MainWindowTitle;
                MostRecentSong = CurrentSong;
            }

            // update timecode
            var oldTimecode = CurrentTimecode;

            if (TimecodeAddress == null || CurrentSong == null)
                CurrentTimecode = null;
            else
            {
                var value = Reader.Default.Read<double>(TimecodeAddress.Value, out var success);
                CurrentTimecode = success ? value : null;
            }

            // notify subscribers
            if (oldSong != CurrentSong)
                SongChanged?.Invoke(oldSong, CurrentSong);
            else if (oldTimecode != CurrentTimecode)
                TimecodeChanged?.Invoke(oldTimecode, CurrentTimecode);

            // update timecode address if not yet set
            if (!TimecodeAddress.HasValue && oldMostRecentSong != MostRecentSong)
            {
                UpdateTimecodeAddress(songStartTime);
            }
        }

        private ulong? TimecodeAddress;

        private CancellationTokenSource TokenSource;

        private void UpdateTimecodeAddress(Stopwatch songStartTime)
        {
            TimecodeAddress = null;

            if (TokenSource != null)
            {
                TokenSource.Cancel();
                Trace.TraceInformation("Aborted already running task for finding timecode address.");
            }

            TokenSource = new CancellationTokenSource();
            var task = Task.Run(() => FindAddress(songStartTime, TokenSource.Token), TokenSource.Token);
            Trace.TraceInformation("Started task for finding timecode address.");
        }

        private void FindAddress(Stopwatch songStartTime, CancellationToken cancellationToken)
        {
            if (Process == null)
                return;

            if (Processes.Default.OpenedProcess == null || Processes.Default.OpenedProcess.Id != Process.Id)
                Processes.Default.OpenedProcess = Process;

            DataType dataType = DataType.Double;

            // Collect values
            Snapshot snapshot = SnapshotManager.GetSnapshot(Snapshot.SnapshotRetrievalMode.FromActiveSnapshotOrPrefilter);
            snapshot.ElementDataType = dataType;

            var timer = new System.Timers.Timer(REFRESHINTERVALADDRESS)
            {
                AutoReset = false
            };

            // read process memory and filter out addresses that fit to the current timecode
            timer.Elapsed += async (sender, e) =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    ScanConstraintCollection scanConstraints = new ScanConstraintCollection();
                    scanConstraints.AddConstraint(new ScanConstraint(ScanConstraint.ConstraintType.LessThanOrEqual, songStartTime.ElapsedMilliseconds / 1000d + 2 * REFRESHINTERVAL / 1000d));
                    scanConstraints.AddConstraint(new ScanConstraint(ScanConstraint.ConstraintType.GreaterThanOrEqual, songStartTime.ElapsedMilliseconds / 1000d - 2 * REFRESHINTERVAL / 1000d));

                    var scanTask = ManualScanner.Scan(snapshot, dataType, scanConstraints, null, out var scanCTS); // further filter snapshot (checks current values)
                    cancellationToken.Register(scanCTS.Cancel);

                    snapshot = await scanTask;
                    cancellationToken.ThrowIfCancellationRequested();

                    if (snapshot.ElementCount == 1 || snapshot.ElementCount == 2) // timecode has been found
                    {
                        TimecodeAddress = snapshot[0].BaseAddress;
                        Trace.TraceInformation("Address of timecode has been found: " + string.Format("0x{0:X}", TimecodeAddress.Value));
                    }
                    else if (snapshot.ElementCount == 0)
                    {
                        Trace.TraceInformation("Address of timecode could not be found.");
                    }
                    if (snapshot.ElementCount <= 2) // timecode wasn't found
                    {
                        timer.Dispose();
                        songStartTime.Stop();
                        return;
                    }

                    timer.Start();
                }
                catch (OperationCanceledException)
                {
                    timer.Dispose();
                    songStartTime.Stop();
                }
            };

            timer.Start();
        }

        private readonly System.Timers.Timer UpdateSongInfoTimer = new System.Timers.Timer(REFRESHINTERVAL)
        {
            AutoReset = true
        };

        public void Start()
        {
            if (!UpdateSongInfoTimer.Enabled)
                UpdateSongInfoTimer.Start();
        }

        public void Stop()
        {
            if (UpdateSongInfoTimer.Enabled)
            {
                UpdateSongInfoTimer.Stop();

                var oldSong = CurrentSong;
                CurrentSong = null;
                CurrentTimecode = null;

                if (oldSong != CurrentSong)
                    SongChanged?.Invoke(oldSong, CurrentSong);
            }
        }

        public void Dispose()
        {
            Stop();
            UpdateSongInfoTimer.Dispose();
        }
    }
}
