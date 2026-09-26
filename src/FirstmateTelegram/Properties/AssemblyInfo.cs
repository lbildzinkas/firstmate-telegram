using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// The bridge relies on Unix file modes and launchd; it runs on macOS, and a Linux service layer is a later addition.
[assembly: UnsupportedOSPlatform("windows")]
[assembly: InternalsVisibleTo("FirstmateTelegram.Tests")]
