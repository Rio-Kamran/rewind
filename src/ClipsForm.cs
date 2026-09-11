using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualBasic;

namespace Rewind
{
    /// <summary>
    /// The Rewind window: a thumbnail grid of every clip (newest first, grouped by day, tiles
    /// stretched to fill the width) with play / show / rename / delete / trim, a live status strip
    /// with the save and pause buttons, and a Settings tab. Built in code; closing it only hides it.
    /// </summary>
    internal sealed class ClipsForm : Form
    {
        private const string AllGames = "All games";

        private readonly IRewindControl _control;
        private readonly string _thumbFolder;
        private readonly ClipGrid _grid = new ClipGrid();
        private readonly ComboBox _gameFilter = new ComboBox();
        private readonly Label _status = new Label();
        private readonly Label _detail = new Label();
        private readonly Button _saveButton = new Button();
        private readonly Button _saveShortButton = new Button();
        private readonly Button _pauseButton = new Button();
        private readonly Button _playButton = new Button();
        private readonly Button _showButton = new Button();
        private readonly Button _renameButton = new Button();
        private readonly Button _deleteButton = new Button();
        private readonly Button _trimButton = new Button();
        private readonly SettingsTab _settings;
        private readonly System.Windows.Forms.Timer _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        private readonly System.Windows.Forms.Timer _deleteArmTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        private readonly System.Windows.Forms.Timer _watchTimer = new System.Windows.Forms.Timer { Interval = 800 };
        private readonly Queue<ClipInfo> _probeQueue = new Queue<ClipInfo>();
        private readonly object _probeGate = new object();
        private FileSystemWatcher _watcher;
        private Thread _probeThread;
        private bool _stopProbe;
        private IList<ClipInfo> _clips = new List<ClipInfo>();
        private bool _deleteArmed;
        private bool _reallyClosing;

        public ClipsForm(IRewindControl control, string appDir)
        {
            if (control == null) throw new ArgumentNullException("control");
            if (string.IsNullOrEmpty(appDir)) throw new ArgumentException("appDir");
            _control = control;
            _thumbFolder = Path.Combine(appDir, "thumbs");
            _settings = new SettingsTab(control);

            Text = "Rewind";
            Size = new Size(1120, 740);
            MinimumSize = new Size(820, 520);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { /* the default icon will do */ }

            var tabs = new TabControl { Dock = DockStyle.Fill };
            var clipsPage = new TabPage("Clips");
            var settingsPage = new TabPage("Settings");
            tabs.TabPages.Add(clipsPage);
            tabs.TabPages.Add(settingsPage);
            Controls.Add(tabs);

            BuildClipsPage(clipsPage);
            _settings.Dock = DockStyle.Fill;
            settingsPage.Controls.Add(_settings);

            _statusTimer.Tick += (s, e) => RefreshStatus();
            _deleteArmTimer.Tick += (s, e) => DisarmDelete();
            _watchTimer.Tick += (s, e) => { _watchTimer.Stop(); RefreshClips(); };
            _control.ClipSaved += clip => RefreshClips();
        }

        /// <summary>Called by TrayApp on quit: after this, Close() really closes.</summary>
        public void AllowClose()
        {
            _reallyClosing = true;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _probeThread = new Thread(ProbeLoop) { IsBackground = true, Name = "rewind-thumbs" };
            _probeThread.Start();
            _settings.LoadFrom(_control.Config);
            RefreshClips();
            RefreshStatus();
            _statusTimer.Start();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && IsHandleCreated && _probeThread != null)
            {
                _settings.LoadFrom(_control.Config);
                RefreshClips();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_reallyClosing && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_probeGate)
                {
                    _stopProbe = true;
                    Monitor.PulseAll(_probeGate);
                }
                _statusTimer.Dispose();
                _deleteArmTimer.Dispose();
                _watchTimer.Dispose();
                if (_watcher != null) _watcher.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- layout ----

        private void BuildClipsPage(TabPage page)
        {
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(6, 8, 6, 0), WrapContents = false };
            _status.AutoSize = true;
            _status.Font = new Font(Font, FontStyle.Bold);
            _status.Margin = new Padding(4, 8, 16, 0);
            _status.Text = "Starting";
            Setup(_saveButton, "Save clip", (s, e) => _control.SaveClip("window", _control.Config.Seconds));
            _saveButton.Font = new Font(Font, FontStyle.Bold);
            Setup(_saveShortButton, "Save last 15 s", (s, e) => _control.SaveClip("window", _control.Config.ShortSeconds));
            Setup(_pauseButton, "Pause", (s, e) => { _control.TogglePause(); RefreshStatus(); });
            var openFolder = new Button();
            Setup(openFolder, "Open folder", (s, e) => OpenFolder());
            var refresh = new Button();
            Setup(refresh, "Refresh", (s, e) => RefreshClips());
            _gameFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            _gameFilter.Width = 180;
            _gameFilter.Margin = new Padding(16, 3, 0, 0);
            _gameFilter.SelectedIndexChanged += (s, e) => FillGrid();
            top.Controls.AddRange(new Control[] { _status, _saveButton, _saveShortButton, _pauseButton, openFolder, refresh, _gameFilter });

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 78, Padding = new Padding(8, 6, 8, 6) };
            _detail.Dock = DockStyle.Fill;
            _detail.Text = "No clip selected.";
            var actions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 470, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 18, 0, 0) };
            Setup(_playButton, "Play", (s, e) => Play());
            _playButton.Font = new Font(Font, FontStyle.Bold);
            Setup(_showButton, "Show in folder", (s, e) => ShowInFolder());
            Setup(_renameButton, "Rename", (s, e) => Rename());
            Setup(_deleteButton, "Delete", (s, e) => DeleteFlow());
            Setup(_trimButton, "Trim…", (s, e) => Trim());
            actions.Controls.AddRange(new Control[] { _playButton, _showButton, _renameButton, _deleteButton, _trimButton });
            bottom.Controls.Add(_detail);
            bottom.Controls.Add(actions);

            _grid.Dock = DockStyle.Fill;
            _grid.SelectionChanged += (s, e) => { DisarmDelete(); UpdateDetail(); };
            _grid.ItemActivated += (s, e) => Play();
            _grid.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete) { DeleteFlow(); e.Handled = true; }
                else if (e.KeyCode == Keys.F2) { Rename(); e.Handled = true; }
            };

            page.Controls.Add(_grid);
            page.Controls.Add(top);
            page.Controls.Add(bottom);
        }

        private static void Setup(Button button, string text, EventHandler onClick)
        {
            button.Text = text;
            button.AutoSize = true;
            button.Padding = new Padding(6, 2, 6, 2);
            button.Margin = new Padding(3, 0, 3, 0);
            button.Click += onClick;
        }

        // ---- the grid ----

        private void RefreshClips()
        {
            if (IsDisposed) return;
            var folder = _control.Config.ClipsFolder;
            try { Directory.CreateDirectory(folder); } catch (Exception) { /* shown as an empty grid */ }
            WatchFolder(folder);
            _clips = ClipLibrary.Scan(folder);
            FillGameFilter();
            FillGrid();
        }

        private void WatchFolder(string folder)
        {
            if (_watcher != null && string.Equals(_watcher.Path, folder, StringComparison.OrdinalIgnoreCase)) return;
            if (_watcher != null) _watcher.Dispose();
            _watcher = null;
            if (!Directory.Exists(folder)) return;
            try
            {
                var watcher = new FileSystemWatcher(folder, "*.mp4")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    SynchronizingObject = this
                };
                watcher.Created += (s, e) => Nudge();
                watcher.Deleted += (s, e) => Nudge();
                watcher.Renamed += (s, e) => Nudge();
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception error)
            {
                Log.Warn("can't watch the clips folder: " + error.Message);
            }
        }

        private void Nudge()
        {
            _watchTimer.Stop();
            _watchTimer.Start();
        }

        private void FillGameFilter()
        {
            var current = _gameFilter.SelectedItem as string ?? AllGames;
            var titles = new List<string>();
            foreach (var clip in _clips)
            {
                var title = clip.Game.Length > 0 ? clip.Game : "Desktop";
                if (!titles.Contains(title)) titles.Add(title);
            }
            titles.Sort(StringComparer.OrdinalIgnoreCase);
            _gameFilter.BeginUpdate();
            _gameFilter.Items.Clear();
            _gameFilter.Items.Add(AllGames);
            foreach (var title in titles) _gameFilter.Items.Add(title);
            _gameFilter.SelectedItem = _gameFilter.Items.Contains(current) ? current : AllGames;
            _gameFilter.EndUpdate();
        }

        private void FillGrid()
        {
            var filter = _gameFilter.SelectedItem as string ?? AllGames;
            var selected = _grid.Selected;
            var shown = new List<ClipInfo>();
            foreach (var clip in _clips)
            {
                var title = clip.Game.Length > 0 ? clip.Game : "Desktop";
                if (filter != AllGames && !string.Equals(filter, title, StringComparison.OrdinalIgnoreCase)) continue;
                shown.Add(clip);
                if (!_grid.HasThumbnail(clip.Path) && _control.FfmpegPath != null) Enqueue(clip); // no ffmpeg yet = no thumbnails yet
            }
            _grid.EmptyText = _clips.Count == 0
                ? "No clips yet.\nPress " + HotkeySpec.Parse(_control.Config.Hotkey).Text + " while something happens."
                : "No clips from " + filter + ".";
            _grid.SetClips(shown.AsReadOnly(), selected != null ? selected.Path : null);
        }

        private static string Describe(ClipInfo clip)
        {
            var length = clip.Duration.HasValue ? Length(clip.Duration.Value) : "length unknown";
            return string.Format("{0}\n{1} · {2} · {3} · {4:0.0} MB", clip.FileName, clip.Title,
                clip.Taken.ToString("ddd d MMM yyyy HH:mm:ss"), length, clip.Bytes / 1048576.0);
        }

        public static string Length(TimeSpan duration)
        {
            return duration.TotalMinutes >= 1
                ? string.Format("{0}:{1:00} min", (int)duration.TotalMinutes, duration.Seconds)
                : string.Format("{0:0.0} s", duration.TotalSeconds);
        }

        private void UpdateDetail()
        {
            var clip = _grid.Selected;
            var have = clip != null;
            _detail.Text = have ? Describe(clip) : (_grid.Count == 0 ? "No clips to show." : "No clip selected.");
            _playButton.Enabled = _showButton.Enabled = _renameButton.Enabled = _deleteButton.Enabled = _trimButton.Enabled = have;
        }

        // ---- thumbnails, off the UI thread ----

        private void Enqueue(ClipInfo clip)
        {
            lock (_probeGate)
            {
                foreach (var queued in _probeQueue) if (queued.Path == clip.Path) return;
                _probeQueue.Enqueue(clip);
                Monitor.Pulse(_probeGate);
            }
        }

        private void ProbeLoop()
        {
            while (true)
            {
                ClipInfo clip;
                lock (_probeGate)
                {
                    while (_probeQueue.Count == 0 && !_stopProbe) Monitor.Wait(_probeGate);
                    if (_stopProbe) return;
                    clip = _probeQueue.Dequeue();
                }
                if (!WaitUntilWritten(clip.Path)) continue; // still growing after a minute, or gone: the next refresh retries
                var result = ClipProbe.Probe(_control.FfmpegPath, clip, _thumbFolder);
                Image image = null;
                if (result.ThumbnailPath != null)
                {
                    try { image = LoadImage(result.ThumbnailPath); }
                    catch (Exception error) { Log.Warn("bad thumbnail for " + clip.FileName + ": " + error.Message); }
                }
                try
                {
                    BeginInvoke(new Action(() => ApplyProbe(clip, result, image)));
                }
                catch (InvalidOperationException)
                {
                    if (image != null) image.Dispose(); // the window is gone
                }
            }
        }

        /// <summary>
        /// A clip shows up in the folder the moment ffmpeg starts writing it; probing it then fails
        /// with "invalid data". Wait until its size has held still for a second (a save takes ~1 s,
        /// a trim a few). False when it never settles or disappears.
        /// </summary>
        private bool WaitUntilWritten(string path)
        {
            long last = -1;
            for (var i = 0; i < 60 && !_stopProbe; i++)
            {
                long size;
                try { size = new FileInfo(path).Length; }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
                if (size > 0 && size == last) return true;
                last = size;
                Thread.Sleep(1000);
            }
            return false;
        }

        /// <summary>Reads a JPEG without keeping the file locked.</summary>
        private static Image LoadImage(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var image = Image.FromStream(stream))
                return new Bitmap(image);
        }

        private void ApplyProbe(ClipInfo clip, ProbeResult result, Image image)
        {
            if (IsDisposed) { if (image != null) image.Dispose(); return; }
            if (image != null) _grid.SetThumbnail(clip.Path, image);
            if (!result.Duration.HasValue) return;
            var updated = clip.WithDuration(result.Duration.Value);
            var clips = new List<ClipInfo>(_clips.Count);
            foreach (var existing in _clips) clips.Add(existing.Path == clip.Path ? updated : existing);
            _clips = clips.AsReadOnly();
            _grid.UpdateClip(updated);
            var selected = _grid.Selected;
            if (selected != null && selected.Path == clip.Path) UpdateDetail();
        }

        // ---- status strip ----

        private void RefreshStatus()
        {
            if (IsDisposed) return;
            var status = _control.Status;
            if (status == null)
            {
                _status.Text = "⏳ Getting ffmpeg (one-time download)";
                _status.ForeColor = Color.DimGray;
                return;
            }
            var missing = status.MissingAudio.Count > 0 ? "  (no " + string.Join(" or ", status.MissingAudio).ToLowerInvariant() + " audio)" : "";
            _status.Text = status.Paused
                ? (status.PauseReason == "waiting for a game" ? "⏳ " : "⏸ ") + status.Text
                : string.Format("● {0} — {1:0} s ready ({2:0} MB){3}", status.Text, status.BufferedSeconds, status.BufferedMb, missing);
            _status.ForeColor = status.Paused ? Color.DimGray : Color.FromArgb(200, 30, 30);
            _pauseButton.Text = _control.UserPaused ? "Resume" : "Pause";
            _saveShortButton.Text = "Save last " + _control.Config.ShortSeconds + " s";
        }

        // ---- actions ----

        private void Play()
        {
            var clip = _grid.Selected;
            if (clip == null) return;
            try { Process.Start(new ProcessStartInfo(clip.Path) { UseShellExecute = true }); }
            catch (Exception error) { Say("Couldn't play it: " + error.Message); }
        }

        private void ShowInFolder()
        {
            var clip = _grid.Selected;
            if (clip == null) return;
            try { Process.Start("explorer.exe", Shell.SelectInExplorerArgs(clip.Path)); }
            catch (Exception error) { Say("Couldn't open the folder: " + error.Message); }
        }

        private void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(_control.Config.ClipsFolder);
                Process.Start("explorer.exe", FfmpegArgs.Quote(_control.Config.ClipsFolder));
            }
            catch (Exception error) { Say("Couldn't open the folder: " + error.Message); }
        }

        private void Rename()
        {
            var clip = _grid.Selected;
            if (clip == null) return;
            var stem = Path.GetFileNameWithoutExtension(clip.Path);
            var wanted = Interaction.InputBox("New name for the clip (without .mp4):", "Rename clip", stem).Trim();
            if (wanted.Length == 0 || wanted == stem) return;
            if (wanted.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Say("That name has characters a file can't have."); return; }
            var target = Path.Combine(Path.GetDirectoryName(clip.Path) ?? "", wanted + ".mp4");
            if (File.Exists(target)) { Say("There's already a clip called that."); return; }
            try
            {
                File.Move(clip.Path, target);
                Log.Info("renamed " + clip.FileName + " -> " + Path.GetFileName(target));
                RefreshClips();
                _grid.Select(target);
            }
            catch (Exception error) { Say("Couldn't rename it: " + error.Message); }
        }

        /// <summary>First click arms the button for three seconds; the second click deletes, to the Recycle Bin.</summary>
        private void DeleteFlow()
        {
            var clip = _grid.Selected;
            if (clip == null) return;
            if (!_deleteArmed)
            {
                _deleteArmed = true;
                _deleteButton.Text = "Really delete?";
                _deleteButton.ForeColor = Color.Firebrick;
                _deleteArmTimer.Start();
                return;
            }
            DisarmDelete();
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(clip.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                Log.Info("deleted (recycle bin): " + clip.FileName);
                RefreshClips();
            }
            catch (Exception error) { Say("Couldn't delete it: " + error.Message); }
        }

        private void DisarmDelete()
        {
            _deleteArmTimer.Stop();
            _deleteArmed = false;
            _deleteButton.Text = "Delete";
            _deleteButton.ForeColor = SystemColors.ControlText;
        }

        private void Trim()
        {
            var clip = _grid.Selected;
            if (clip == null) return;
            if (_control.FfmpegPath == null) { Say("Rewind is still getting ffmpeg; try again in a minute."); return; }
            using (var dialog = new TrimForm(_control, clip, _thumbFolder))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SavedPath == null) return;
                RefreshClips();
                _grid.Select(dialog.SavedPath);
            }
        }

        private void Say(string message)
        {
            MessageBox.Show(this, message, "Rewind", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
