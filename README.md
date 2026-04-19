# Network IPs for macOS

Minimal native macOS app that shows:

- Public IP
- Local IP
- Tailscale detection
- Traceroute to the current public IP

## Files

- `ExternalIPApp/main.swift` - app source
- `ExternalIPApp/Info.plist` - bundle metadata
- `ExternalIPApp/IconGenerator.swift` - generates the Art Deco app icon

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

## Notes

- The app uses `https://api.ipify.org?format=json` for the public IP lookup.
- Tailscale detection prefers the `tailscale` CLI when present and falls back to interface scanning.
- Traceroute runs through `/usr/sbin/traceroute`.

## License

MIT
