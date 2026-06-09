# Denia Memory Forensics

Denia Memory Forensics is a native Windows C# front-end for practical memory forensics workflows. It keeps the interface fast and responsive while delegating deep Volatility analysis to a portable Volatility/Battle CLI engine when available.

## Current features

- Native WPF dashboard for Windows.
- Drag and drop memory dump selection.
- Battle console runner for Volatility/Battle CLI commands.
- File tree renderer through the configured Volatility/Battle engine.
- Native C# image carver for JPG, PNG, GIF, BMP, ICO, WEBP, and TIFF signatures.
- VirusTotal v3 hash reputation checks without uploading files.
- Portable settings and output folders.

## Build

```powershell
dotnet build
```

## Run

```powershell
dotnet run
```

## Optional Volatility engine

The app can auto-detect the previous Python tool at:

```text
..\volatility3-2.26.2\dist\Volatility3Analyzer.exe
```

You can also set the engine path from the Settings view. The recommended engine is the existing `Volatility3Analyzer.exe` because it exposes the Battle CLI commands used by this UI.

## Notes

Do not commit memory dumps, dumped process memory, carved images, API keys, or build output. The repository `.gitignore` blocks those by default.
