using System;
using System.Collections.Generic;

namespace Rewind
{
    /// <summary>
    /// What the window is allowed to do to the running Rewind. TrayApp implements it; the forms
    /// only ever see this, so they can't reach into the pipeline.
    /// </summary>
    internal interface IRewindControl
    {
        Config Config { get; }
        string FfmpegPath { get; }
        /// <summary>The folder Rewind.exe runs from (custom sounds, thumbnails).</summary>
        string AppDir { get; }
        SessionStatus Status { get; }
        bool UserPaused { get; }
        /// <summary>A long recording is running.</summary>
        bool Recording { get; }

        void SaveClip(string reason, int seconds);
        void TogglePause();
        /// <summary>Starts a long recording, or stops the one running.</summary>
        void ToggleRecording(string reason);
        void TakeScreenshot(string reason);
        /// <summary>Plays the clip chime at the current settings (the Settings tab's Test button).</summary>
        void PlayTestSound();

        /// <summary>Validates, writes config.txt and restarts capture. Throws ConfigException with a message to show.</summary>
        void SaveSettings(IDictionary<string, string> values);

        /// <summary>Raised on the UI thread after every clip that lands.</summary>
        event Action<SavedClip> ClipSaved;
        /// <summary>Raised on the UI thread after a screenshot or a finished recording lands (the grid refreshes).</summary>
        event Action FilesChanged;
    }
}
