using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Rewind
{
    /// <summary>
    /// Pick a start and an end on a clip and save just that stretch as a new file. The frame under
    /// the slider being dragged shows in the preview (fetched off the UI thread, one at a time);
    /// the save re-encodes on NVENC so the cut is frame-exact. The original is never touched.
    /// </summary>
    internal sealed class TrimForm : Form
    {
        private const int TicksPerSecond = 10;
        private const double MinLengthSeconds = 0.5;
        private const int SaveTimeoutMs = 300000;

        private readonly IRewindControl _control;
        private readonly string _thumbFolder;
        private readonly PictureBox _preview = new PictureBox();
        private readonly TrackBar _start = new TrackBar();
        private readonly TrackBar _end = new TrackBar();
        private readonly Label _startLabel = new Label();
        private readonly Label _endLabel = new Label();
        private readonly Label _lengthLabel = new Label();
        private readonly Label _message = new Label();
        private readonly Button _save = new Button();
        private readonly Button _gif = new Button();
        private readonly Button _cancel = new Button();
        private const double MaxGifSeconds = 20;
        private readonly System.Windows.Forms.Timer _previewTimer = new System.Windows.Forms.Timer { Interval = 150 };
        private ClipInfo _clip;
        private double _previewAt;
        private double _shownAt = -1;
        private bool _fetching;
        private bool _saving;

        /// <summary>The new file, once saved.</summary>
        public string SavedPath { get; private set; }

        public TrimForm(IRewindControl control, ClipInfo clip, string thumbFolder)
        {
            if (control == null) throw new ArgumentNullException("control");
            if (clip == null) throw new ArgumentNullException("clip");
            _control = control;
            _clip = clip;
            _thumbFolder = thumbFolder;

            Text = "Trim — " + clip.FileName;
            Size = new Size(760, 640);
            MinimumSize = new Size(600, 520);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            _preview.Dock = DockStyle.Fill;
            _preview.BackColor = Color.Black;
            _preview.SizeMode = PictureBoxSizeMode.Zoom;

            var controls = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(10, 6, 10, 8) };
            controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            SetupBar(_start);
            SetupBar(_end);
            _startLabel.AutoSize = true; _endLabel.AutoSize = true; _lengthLabel.AutoSize = true;
            _lengthLabel.Font = new Font(Font, FontStyle.Bold);
            _message.AutoSize = true;
            _message.ForeColor = SystemColors.GrayText;
            _message.Text = "Drag the sliders: the picture shows the frame under the one you're moving. Left/Right keys nudge by 0.1 s.";

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            _save.Text = "Save trimmed copy";
            _save.Font = new Font(Font, FontStyle.Bold);
            _save.AutoSize = true;
            _save.Click += (s, e) => Save();
            _gif.Text = "Save as GIF";
            _gif.AutoSize = true;
            _gif.Click += (s, e) => SaveGif();
            _cancel.Text = "Cancel";
            _cancel.AutoSize = true;
            _cancel.Click += (s, e) => { if (!_saving) DialogResult = DialogResult.Cancel; };
            buttons.Controls.Add(_save);
            buttons.Controls.Add(_gif);
            buttons.Controls.Add(_cancel);

            controls.Controls.Add(_startLabel, 0, 0);
            controls.Controls.Add(_start, 1, 0);
            controls.Controls.Add(_endLabel, 0, 1);
            controls.Controls.Add(_end, 1, 1);
            controls.Controls.Add(_lengthLabel, 0, 2);
            controls.Controls.Add(_message, 1, 2);
            controls.Controls.Add(buttons, 1, 3);

            Controls.Add(_preview);
            Controls.Add(controls);
            CancelButton = _cancel;

            _previewTimer.Tick += (s, e) => { _previewTimer.Stop(); FetchPreview(); };
        }

        private void SetupBar(TrackBar bar)
        {
            bar.Dock = DockStyle.Fill;
            bar.TickStyle = TickStyle.None;
            bar.SmallChange = 1;
            bar.LargeChange = TicksPerSecond;
            bar.Enabled = false;
            bar.ValueChanged += (s, e) => OnBarMoved(bar);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (_clip.Duration.HasValue) Ready(_clip.Duration.Value);
            else
            {
                _lengthLabel.Text = "Reading…";
                var clip = _clip;
                var worker = new Thread(() =>
                {
                    var result = ClipProbe.Probe(_control.FfmpegPath, clip, _thumbFolder);
                    try { BeginInvoke(new Action(() => { if (result.Duration.HasValue) Ready(result.Duration.Value); else Fail("Couldn't read how long the clip is."); })); }
                    catch (InvalidOperationException) { }
                }) { IsBackground = true, Name = "rewind-trim-probe" };
                worker.Start();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _previewTimer.Dispose();
                var old = _preview.Image;
                _preview.Image = null;
                if (old != null) old.Dispose();
            }
            base.Dispose(disposing);
        }

        private void Ready(TimeSpan duration)
        {
            _clip = _clip.WithDuration(duration);
            var ticks = Math.Max(TicksPerSecond, (int)Math.Floor(duration.TotalSeconds * TicksPerSecond));
            _start.Maximum = ticks;
            _end.Maximum = ticks;
            _start.Value = 0;
            _end.Value = ticks;
            _start.Enabled = _end.Enabled = true;
            UpdateLabels();
            _previewAt = 0;
            FetchPreview();
        }

        private void OnBarMoved(TrackBar bar)
        {
            var minGap = (int)Math.Ceiling(MinLengthSeconds * TicksPerSecond);
            if (bar == _start && _start.Value > _end.Value - minGap) _start.Value = Math.Max(0, _end.Value - minGap);
            if (bar == _end && _end.Value < _start.Value + minGap) _end.Value = Math.Min(_end.Maximum, _start.Value + minGap);
            UpdateLabels();
            _previewAt = bar.Value / (double)TicksPerSecond;
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private void UpdateLabels()
        {
            _startLabel.Text = "Start  " + Seconds(_start.Value);
            _endLabel.Text = "End  " + Seconds(_end.Value);
            _lengthLabel.Text = "Length  " + Seconds(_end.Value - _start.Value);
        }

        private static string Seconds(int ticks)
        {
            return (ticks / (double)TicksPerSecond).ToString("0.0") + " s";
        }

        // ---- preview ----

        private void FetchPreview()
        {
            if (_fetching || IsDisposed || Math.Abs(_previewAt - _shownAt) < 0.001) return;
            _fetching = true;
            var at = _previewAt;
            var path = _clip.Path;
            var worker = new Thread(() =>
            {
                var bytes = ClipProbe.FrameAt(_control.FfmpegPath, path, at);
                Image image = null;
                if (bytes.Length > 0)
                {
                    try { using (var stream = new MemoryStream(bytes)) using (var decoded = Image.FromStream(stream)) image = new Bitmap(decoded); }
                    catch (Exception error) { Log.Warn("preview frame unreadable: " + error.Message); }
                }
                try { BeginInvoke(new Action(() => ShowPreview(at, image))); }
                catch (InvalidOperationException) { if (image != null) image.Dispose(); }
            }) { IsBackground = true, Name = "rewind-trim-preview" };
            worker.Start();
        }

        private void ShowPreview(double at, Image image)
        {
            _fetching = false;
            if (IsDisposed) { if (image != null) image.Dispose(); return; }
            if (image != null)
            {
                var old = _preview.Image;
                _preview.Image = image;
                if (old != null) old.Dispose();
                _shownAt = at;
            }
            if (Math.Abs(_previewAt - _shownAt) >= 0.001) FetchPreview(); // the slider moved on while we fetched
        }

        // ---- save ----

        private void Save()
        {
            if (_saving || !_clip.Duration.HasValue) return;
            var startSeconds = _start.Value / (double)TicksPerSecond;
            var lengthSeconds = (_end.Value - _start.Value) / (double)TicksPerSecond;
            if (lengthSeconds < MinLengthSeconds) { Fail("Pick at least half a second."); return; }
            var output = ClipLibrary.CopyName(_clip.Path, "trim", File.Exists);
            Export("trim", output, FfmpegArgs.Trim(_clip.Path, output, startSeconds, lengthSeconds, _control.Config), false);
        }

        /// <summary>Medal's GIF export: the same stretch as a small looping GIF, copied so it pastes straight into Discord.</summary>
        private void SaveGif()
        {
            if (_saving || !_clip.Duration.HasValue) return;
            var startSeconds = _start.Value / (double)TicksPerSecond;
            var lengthSeconds = (_end.Value - _start.Value) / (double)TicksPerSecond;
            if (lengthSeconds < MinLengthSeconds) { Fail("Pick at least half a second."); return; }
            if (lengthSeconds > MaxGifSeconds) { Fail("A GIF this long would be huge. Keep it under " + MaxGifSeconds + " s."); return; }
            var output = ClipLibrary.CopyName(_clip.Path, "", "gif", File.Exists);
            Export("GIF", output, FfmpegArgs.Gif(_clip.Path, output, startSeconds, lengthSeconds), true);
        }

        private void Export(string what, string output, string args, bool copy)
        {
            _saving = true;
            _save.Enabled = _gif.Enabled = _cancel.Enabled = _start.Enabled = _end.Enabled = false;
            _message.ForeColor = SystemColors.GrayText;
            _message.Text = "Saving " + Path.GetFileName(output) + "…";
            var ffmpeg = _control.FfmpegPath;
            var worker = new Thread(() =>
            {
                string error = null;
                try { FfmpegRun.Execute(ffmpeg, args, output, SaveTimeoutMs); }
                catch (Exception failure) { error = failure.Message; }
                try { BeginInvoke(new Action(() => Saved(what, output, error, copy))); }
                catch (InvalidOperationException) { }
            }) { IsBackground = true, Name = "rewind-trim-save" };
            worker.Start();
        }

        private void Saved(string what, string output, string error, bool copy)
        {
            _saving = false;
            _save.Enabled = _gif.Enabled = _cancel.Enabled = _start.Enabled = _end.Enabled = true;
            if (error != null)
            {
                Log.Error(what + " failed: " + error);
                Fail("Couldn't save the " + what + ": " + error);
                return;
            }
            Log.Info(string.Format("{0}: {1} -> {2} ({3:0.0} MB)", what, _clip.FileName, Path.GetFileName(output), new FileInfo(output).Length / 1048576.0));
            if (copy) ClipsForm.CopyFileToClipboard(output);
            SavedPath = output;
            DialogResult = DialogResult.OK;
        }

        private void Fail(string message)
        {
            _message.ForeColor = Color.Firebrick;
            _message.Text = message;
        }
    }
}
