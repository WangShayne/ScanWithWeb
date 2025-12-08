# ScanWithWeb v2.0

[![Build](https://github.com/user/scanwithweb/actions/workflows/build-release.yml/badge.svg)](https://github.com/user/scanwithweb/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A modern web-enabled scanner service that bridges web browsers and local TWAIN scanners via secure WebSocket communication.

[中文文档](./README.zh-CN.md)

## Downloads

| Platform | Download | Description |
|----------|----------|-------------|
| Windows 64-bit | [ScanWithWeb_Setup_x64.exe](https://github.com/user/scanwithweb/releases/latest) | Recommended for most users |
| Windows 32-bit | [ScanWithWeb_Setup_x86.exe](https://github.com/user/scanwithweb/releases/latest) | For 32-bit systems |
| Portable 64-bit | [ScanWithWeb_Portable_x64.zip](https://github.com/user/scanwithweb/releases/latest) | No installation required |
| Portable 32-bit | [ScanWithWeb_Portable_x86.zip](https://github.com/user/scanwithweb/releases/latest) | No installation required |

## Features

- **Dual WebSocket Support** - WS (port 8180) for HTTP pages, WSS (port 8181) for HTTPS pages
- **Auto SSL Certificate** - Self-signed certificate auto-installed to Windows trusted store
- **Token Authentication** - Session-based security with automatic expiration
- **No Broadcast** - Images sent only to requesting client
- **Full Remote Control** - Configure DPI, color, paper size via JSON protocol
- **Multi-page Scanning** - ADF (Automatic Document Feeder) support
- **System Tray** - Runs silently in background with notifications
- **Built-in Test Page** - Test scanner connection without writing code
- **.NET 8** - Modern runtime with single-file deployment
- **TypeScript SDK** - Full type definitions for React, Vue, and vanilla JS

## Quick Start

### 1. Install the Service

Download and run the installer from the [Releases](https://github.com/user/scanwithweb/releases) page.

Or build from source:
```bash
cd ScanWithWeb
dotnet publish -c Release -r win-x64 -o dist/win-x64 --self-contained true
```

### 2. Install Web SDK

```bash
npm install scanwith-web-sdk
```

### 3. Web Integration

**React:**
```tsx
import { useScanClient, useScanners, useScan } from 'scanwith-web-sdk/react';

function ScannerApp() {
  const { client, isConnected, isAuthenticated } = useScanClient({
    secure: true,
    autoConnect: true,
  });

  const { scanners, selectScanner } = useScanners(client);
  const { scan, images, scanning } = useScan(client);

  return (
    <div>
      <select onChange={(e) => selectScanner(e.target.value)}>
        {scanners.map((s) => (
          <option key={s.id} value={s.name}>{s.name}</option>
        ))}
      </select>
      <button onClick={() => scan({ dpi: 300 })} disabled={scanning}>
        Scan
      </button>
      {images.map((img, i) => (
        <img key={i} src={URL.createObjectURL(img.blob)} />
      ))}
    </div>
  );
}
```

**Vue 3:**
```vue
<script setup>
import { useFullScanner } from 'scanwith-web-sdk/vue';

const { isConnected, scanners, scanning } = useFullScanner({ secure: true });
</script>

<template>
  <select @change="scanners.selectScanner($event.target.value)">
    <option v-for="s in scanners.scanners" :key="s.id" :value="s.name">
      {{ s.name }}
    </option>
  </select>
  <button @click="scanning.scan({ dpi: 300 })">Scan</button>
</template>
```

**Vanilla JavaScript:**
```javascript
import { ScanClient } from 'scanwith-web-sdk';

const client = new ScanClient({ secure: true });

client.on('image', (image) => {
  document.getElementById('preview').src = URL.createObjectURL(image.blob);
});

await client.connect();
await client.authenticate();
await client.scan({ dpi: 300, pixelType: 'RGB' });
```

## Project Structure

```
ScanWithWebForWeb/
├── ScanWithWeb/                  # .NET 8 Windows Service
│   ├── Models/Protocol.cs        # JSON protocol definitions
│   ├── Services/
│   │   ├── SessionManager.cs     # Token authentication
│   │   ├── ScannerService.cs     # TWAIN operations
│   │   └── DualWebSocketService.cs # WS/WSS server
│   ├── Resources/TestPage.html   # Built-in test page
│   ├── MainForm.cs               # UI with system tray
│   └── appsettings.json          # Configuration
├── packages/
│   └── scanwith-web-sdk/         # NPM Package (TypeScript SDK)
│       ├── src/
│       │   ├── core/             # Core client
│       │   ├── react/            # React hooks & components
│       │   ├── vue/              # Vue composables
│       │   └── types/            # TypeScript definitions
│       └── examples/             # Usage examples
├── ScanWithWeb_setup/            # Installer scripts (Inno Setup)
├── assets/                       # Application icons
└── .github/workflows/            # CI/CD workflows
```

## Configuration

Edit `ScanWithWeb/appsettings.json`:

```json
{
  "WebSocket": {
    "WsPort": 8180,
    "WssPort": 8181,
    "CertificatePath": "certificate.pfx",
    "CertificatePassword": "ScanWithWeb"
  },
  "Session": {
    "TokenExpirationMinutes": 60,
    "MaxConcurrentSessions": 10
  }
}
```

**Note:** SSL certificate is automatically generated and installed to Windows trusted store on first run.

## Protocol V2

### Request
```json
{
  "action": "scan",
  "token": "your-auth-token",
  "settings": {
    "dpi": 300,
    "pixelType": "RGB",
    "paperSize": "A4",
    "useAdf": true,
    "maxPages": -1
  }
}
```

### Response
```json
{
  "status": "success",
  "metadata": { "width": 2480, "height": 3508, "dpi": 300 },
  "data": "base64-image-data",
  "pageNumber": 1
}
```

### Available Actions

| Action | Description |
|--------|-------------|
| `authenticate` | Get session token |
| `list_scanners` | Get available scanners |
| `select_scanner` | Select a scanner |
| `get_capabilities` | Get scanner capabilities |
| `scan` | Start scanning |
| `stop_scan` | Stop current scan |

### Scan Settings

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `dpi` | number | 200 | Resolution in DPI |
| `pixelType` | string | "RGB" | Color mode (RGB, Gray8, BlackWhite) |
| `paperSize` | string | "A4" | Paper size (A4, Letter, Legal, etc.) |
| `duplex` | boolean | false | Two-sided scanning |
| `useAdf` | boolean | true | Use document feeder |
| `maxPages` | number | -1 | Max pages (-1 = unlimited) |
| `showUI` | boolean | false | Show scanner dialog |

## Web SDK

The TypeScript SDK provides full support for React, Vue, and vanilla JavaScript:

### Features
- Full TypeScript support with type definitions
- React hooks (`useScanClient`, `useScanners`, `useScan`)
- Vue 3 composables (`useFullScanner`, `useScanClient`)
- Event-driven architecture
- Auto-reconnection support
- Error handling with typed errors

### Installation

```bash
npm install scanwith-web-sdk
```

See [packages/scanwith-web-sdk/README.md](packages/scanwith-web-sdk/README.md) for full documentation.

## Building from Source

### Windows Service

```bash
# 64-bit
dotnet publish ScanWithWeb/ScanWithWeb.csproj -c Release -r win-x64 -o dist/win-x64 --self-contained true

# 32-bit
dotnet publish ScanWithWeb/ScanWithWeb.csproj -c Release -r win-x86 -o dist/win-x86 --self-contained true
```

### Creating Installers

See [ScanWithWeb_setup/README.md](ScanWithWeb_setup/README.md) for instructions on building installers with Inno Setup.

### Web SDK

```bash
cd packages/scanwith-web-sdk
npm install
npm run build
```

## CI/CD

This project uses GitHub Actions for automated builds:

- **Build Test**: Runs on push/PR to verify build succeeds
- **Build and Release**: Creates installers and releases when a version tag is pushed

To create a release:
```bash
git tag v2.0.3
git push origin v2.0.3
```

## Requirements

- Windows 10/11
- .NET 8.0 Runtime (included in installer)
- TWAIN-compatible scanner

## Browser Support

| Browser | Version |
|---------|---------|
| Chrome | 60+ |
| Firefox | 55+ |
| Safari | 11+ |
| Edge | 79+ |

## License

MIT License
