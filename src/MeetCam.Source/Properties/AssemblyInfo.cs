using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

#if DEBUG
[assembly: AssemblyConfiguration("DEBUG")]
#else
[assembly: AssemblyConfiguration("RELEASE")]
#endif
[assembly: AssemblyTitle("MeetCam.Source")]
[assembly: AssemblyDescription("MeetCam virtual camera media source")]
[assembly: AssemblyProduct("MeetCam")]
[assembly: AssemblyCopyright("Camera host adapted from VCamNetSample, Copyright (C) 2023-2026 Simon Mourier.")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]
[assembly: Guid("6e4a1c90-2b57-4f18-9d33-0a8c5e71b246")]
[assembly: SupportedOSPlatform("windows10.0.22621.0")]
