# Appends to the release notes how to download and start each version (Windows, Mac, Linux).
# Called by publierRelease.bat once the three zips exist; does nothing if the section is already there.
param([string]$NotesPath)
$marker = '## Downloads'
$notes = if (Test-Path -LiteralPath $NotesPath) { [IO.File]::ReadAllText($NotesPath) } else { '' }
if ($notes.Contains($marker)) { exit 0 }
$section = @"


$marker

- **Windows**: ``DigitalLogicSim-Windows.zip``, unzip and run ``DigitalLogicSim.exe``.
- **macOS** (Intel and Apple silicon): ``DigitalLogicSim-Mac.zip``, unzip and move ``DigitalLogicSim.app`` where you like.
  The app is not signed by Apple, so macOS refuses it the first time ("damaged" or "unidentified developer"):
  right-click it > Open, or run ``xattr -cr DigitalLogicSim.app`` in a terminal once.
- **Linux** (x86-64): ``DigitalLogicSim-Linux.zip``, unzip and run ``DigitalLogicSim/DigitalLogicSim.x86_64``.

The Mac and Linux versions are built from Windows without the Burst compiler: same behaviour, slower simulation
than the Windows version.
"@
[IO.File]::WriteAllText($NotesPath, $notes.TrimEnd() + $section, (New-Object Text.UTF8Encoding($false)))
