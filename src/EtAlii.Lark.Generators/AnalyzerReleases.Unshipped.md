; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
LARK001 | EtAlii.Lark | Error | The grammar class must be partial
LARK002 | EtAlii.Lark | Error | The grammar file was not found
LARK003 | EtAlii.Lark | Warning | The grammar could not be analyzed
LARK004 | EtAlii.Lark | Error | Specify one grammar source
LARK005 | EtAlii.Lark | Error | Unknown name in [LarkResult]
LARK006 | EtAlii.Lark | Error | Name mapped more than once
LARK007 | EtAlii.Lark | Warning | No common result type
LARK008 | EtAlii.Lark | Error | Result type doesn't fit the inlined rule
LARK009 | EtAlii.Lark | Info | Node children have no fixed shape
