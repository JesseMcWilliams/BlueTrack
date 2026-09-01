using System.Runtime.Versioning;

// Single Windows server, domain-joined, Windows Integrated auth throughout
// (D-09, D-30): this app is not cross-platform by design, so Windows-only
// APIs (WindowsIdentity.Groups, SecurityIdentifier, etc.) are expected and
// safe to call anywhere in this assembly.
[assembly: SupportedOSPlatform("windows")]
