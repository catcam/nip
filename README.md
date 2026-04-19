# Network IPs

Minimal desktop utility set that shows:

- Public IP
- Local IP
- Tailscale detection
- Traceroute to the current public IP

Windows portable build included:

- `dist/NetworkIPs.exe`

## Files

- `ExternalIPApp/main.swift` - app source
- `ExternalIPApp/Info.plist` - bundle metadata
- `ExternalIPApp/IconGenerator.swift` - generates the Art Deco app icon
- `NetworkIPs.Windows/` - Avalonia Windows app source
- `dist/NetworkIPs.exe` - portable Windows executable

## Build

Apple Silicon:

```bash
swiftc ExternalIPApp/main.swift -o /tmp/ExternalIP.app/Contents/MacOS/ExternalIP -framework AppKit -framework Foundation
```

Intel:

```bash
swiftc -target x86_64-apple-macos13.0 ExternalIPApp/main.swift -o /tmp/ExternalIP-x86_64 -framework AppKit -framework Foundation
```

Icon:

```bash
swiftc ExternalIPApp/IconGenerator.swift -o /tmp/IconGenerator -framework AppKit -framework Foundation
/tmp/IconGenerator /tmp/AppIcon.iconset ExternalIPApp/AppIcon.icns
```

Windows portable `.exe`:

```bash
export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec
export PATH=$DOTNET_ROOT:$PATH
dotnet publish NetworkIPs.Windows/NetworkIPs.Windows.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:PublishTrimmed=true /p:TrimMode=partial /p:DebugType=None /p:DebugSymbols=false
cp NetworkIPs.Windows/bin/Release/net10.0/win-x64/publish/NetworkIPs.Windows.exe dist/NetworkIPs.exe
```

## Notes

- The app uses `https://api.ipify.org?format=json` for the public IP lookup.
- Tailscale detection prefers the `tailscale` CLI when present and falls back to interface scanning.
- Traceroute runs through `/usr/sbin/traceroute`.
- The Windows build uses `tracert`.
- The committed Windows `.exe` is the trimmed portable build to keep size down.

## License

MIT
