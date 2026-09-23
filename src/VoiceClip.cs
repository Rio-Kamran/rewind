using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Speech.Recognition;

namespace Rewind
{
    /// <summary>Something that listens on the mic for the clip phrase. Two engines: Windows' own recogniser, or RioVoice.</summary>
    internal interface IVoiceTrigger : IDisposable
    {
        /// <summary>"listening", or the reason it isn't.</summary>
        string Status { get; }
        bool Listening { get; }
        /// <summary>Opens the mic and starts listening. False (with Status saying why) when it can't.</summary>
        bool TryStart();
    }

    /// <summary>
    /// Medal's "clip that" on Windows' own offline speech recogniser: it listens on the default
    /// mic for one phrase, and hearing it counts as pressing the clip hotkey. A one-phrase grammar
    /// plus a confidence bar keeps random chatter from clipping. Nothing leaves the PC.
    /// </summary>
    internal sealed class WindowsVoice : IVoiceTrigger
    {
        public const double MinConfidence = 0.85;

        private readonly string _phrase;
        private readonly Action<string, float> _onHeard;
        private SpeechRecognitionEngine _engine;
        private volatile string _status = "off";

        public WindowsVoice(string phrase, Action<string, float> onHeard)
        {
            if (string.IsNullOrEmpty(phrase)) throw new ArgumentException("phrase");
            if (onHeard == null) throw new ArgumentNullException("onHeard");
            _phrase = phrase;
            _onHeard = onHeard;
        }

        public string Status { get { return _status; } }
        public bool Listening { get { return _status == "listening"; } }

        public bool TryStart()
        {
            Stop();
            SpeechRecognitionEngine engine = null;
            try
            {
                engine = new SpeechRecognitionEngine();
                var builder = new GrammarBuilder(_phrase) { Culture = engine.RecognizerInfo.Culture };
                engine.LoadGrammar(new Grammar(builder) { Name = "rewind-clip" });
                engine.SetInputToDefaultAudioDevice();
                engine.SpeechRecognized += OnRecognized;
                engine.RecognizeCompleted += OnCompleted;
                engine.RecognizeAsync(RecognizeMode.Multiple);
                _engine = engine;
                _status = "listening";
                Log.Info(string.Format("voice (windows): listening for \"{0}\" ({1})", _phrase, engine.RecognizerInfo.Description));
                return true;
            }
            catch (InvalidOperationException error) { return Failed(engine, error.Message); }
            catch (ArgumentException error) { return Failed(engine, error.Message); }
            catch (PlatformNotSupportedException error) { return Failed(engine, error.Message); }
            catch (COMException error) { return Failed(engine, error.Message); }
            catch (FormatException error) { return Failed(engine, error.Message); }
        }

        private bool Failed(SpeechRecognitionEngine engine, string message)
        {
            if (engine != null) engine.Dispose();
            _status = message.Length > 0 ? message : "couldn't start";
            Log.Warn("voice (windows): not listening: " + _status);
            return false;
        }

        private void OnRecognized(object sender, SpeechRecognizedEventArgs e)
        {
            var confidence = e.Result.Confidence;
            var heard = e.Result.Text;
            if (confidence < MinConfidence)
            {
                Log.Info(string.Format(CultureInfo.InvariantCulture, "voice (windows): heard \"{0}\" at {1:0.00}, under the {2:0.00} bar; ignored", heard, confidence, MinConfidence));
                return;
            }
            Log.Info(string.Format(CultureInfo.InvariantCulture, "voice (windows): \"{0}\" ({1:0.00})", heard, confidence));
            _onHeard(heard, confidence);
        }

        private void OnCompleted(object sender, RecognizeCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                _status = e.Error.Message;
                Log.Warn("voice (windows): stopped: " + e.Error.Message);
            }
            else if (e.InputStreamEnded)
            {
                _status = "the mic went away";
                Log.Warn("voice (windows): the mic went away; will retry");
            }
        }

        public void Stop()
        {
            var engine = _engine;
            _engine = null;
            if (engine == null) return;
            try
            {
                engine.SpeechRecognized -= OnRecognized;
                engine.RecognizeCompleted -= OnCompleted;
                engine.RecognizeAsyncCancel();
                engine.Dispose();
            }
            catch (InvalidOperationException) { }
            catch (COMException) { }
            _status = "off";
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
