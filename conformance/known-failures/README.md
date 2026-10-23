Each port may list the conformance cases it doesn't pass yet in `<port>.txt` (`dotnet.txt`, `rust.txt`,
`typescript.txt`): one case id per line, optionally followed by a reason, `#` starts a comment. A listed case that
starts passing fails the run, so the lists only ever shrink.
