// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Interoperability", "SYSLIB1054:Use 'LibraryImportAttribute' instead of 'DllImportAttribute' to generate P/Invoke marshalling code at compile time", Justification = "LibraryImport requires using unsafe code blocks")]
[assembly: SuppressMessage("Style", "IDE0074:Use compound assignment", Justification = "Compound assignment isn't clear to me, would rather have explicit '== null' check")]
