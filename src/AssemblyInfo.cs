using System.Reflection;
using System.Runtime.InteropServices;

// What Explorer shows under Properties -> Details. The version itself is stamped by build.ps1
// from the git tag (obj\Version.cs), so it is never edited by hand.
[assembly: AssemblyTitle("Rewind")]
[assembly: AssemblyDescription("Rewind: a replay buffer that saves the last minute of the screen as a clip (a homemade Medal).")]
[assembly: AssemblyProduct("Rewind")]
[assembly: AssemblyCompany("RioMax")]
[assembly: AssemblyCopyright("Copyright (c) 2026 RioMax, MIT License")]
[assembly: ComVisible(false)]
