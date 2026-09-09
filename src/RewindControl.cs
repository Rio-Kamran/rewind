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
        SessionStatus Status { get; }
        bool UserPaused { get; }

        void SaveClip(string reason, int seconds);
        void TogglePause();

        /// <summary>Validates, writes config.txt and restarts capture. Throws ConfigException with a message to show.</summary>
        void SaveSettings(IDictionary<string, string> values);

        /// <summary>Raised on the UI thread after every clip that lands.</summary>
        event Action<SavedClip> ClipSaved;
    }
}
