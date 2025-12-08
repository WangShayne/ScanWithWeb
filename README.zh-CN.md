# ScanWithWeb v2.0

[![构建状态](https://github.com/user/scanwithweb/actions/workflows/build-release.yml/badge.svg)](https://github.com/user/scanwithweb/actions)
[![许可证: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

一个现代化的 Web 扫描仪服务，通过安全的 WebSocket 通信连接 Web 浏览器和本地 TWAIN 扫描仪。

[English Documentation](./README.md)

## 下载

| 平台 | 下载 | 说明 |
|------|------|------|
| Windows 64位 | [ScanWithWeb_Setup_x64.exe](https://github.com/user/scanwithweb/releases/latest) | 推荐大多数用户使用 |
| Windows 32位 | [ScanWithWeb_Setup_x86.exe](https://github.com/user/scanwithweb/releases/latest) | 适用于32位系统 |
| 便携版 64位 | [ScanWithWeb_Portable_x64.zip](https://github.com/user/scanwithweb/releases/latest) | 无需安装 |
| 便携版 32位 | [ScanWithWeb_Portable_x86.zip](https://github.com/user/scanwithweb/releases/latest) | 无需安装 |

## 特性

- **双协议 WebSocket** - WS (端口 8180) 用于 HTTP 页面，WSS (端口 8181) 用于 HTTPS 页面
- **自动 SSL 证书** - 自签名证书自动安装到 Windows 受信任存储
- **令牌认证** - 基于会话的安全机制，支持自动过期
- **无广播** - 图像仅发送给请求的客户端
- **完全远程控制** - 通过 JSON 协议配置 DPI、色彩、纸张大小
- **多页扫描** - 支持 ADF（自动进纸器）
- **系统托盘** - 后台静默运行并显示通知
- **内置测试页面** - 无需编写代码即可测试扫描仪连接
- **.NET 8** - 现代运行时，单文件部署
- **TypeScript SDK** - 完整的类型定义，支持 React、Vue 和原生 JS

## 快速开始

### 1. 安装服务

从 [Releases](https://github.com/user/scanwithweb/releases) 页面下载并运行安装程序。

或从源码构建：
```bash
cd ScanWithWeb
dotnet publish -c Release -r win-x64 -o dist/win-x64 --self-contained true
```

### 2. 安装 Web SDK

```bash
npm install scanwith-web-sdk
```

### 3. Web 集成

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
        扫描
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
  <button @click="scanning.scan({ dpi: 300 })">扫描</button>
</template>
```

**原生 JavaScript:**
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

## 项目结构

```
ScanWithWebForWeb/
├── ScanWithWeb/                  # .NET 8 Windows 服务
│   ├── Models/Protocol.cs        # JSON 协议定义
│   ├── Services/
│   │   ├── SessionManager.cs     # 令牌认证
│   │   ├── ScannerService.cs     # TWAIN 操作
│   │   └── DualWebSocketService.cs # WS/WSS 服务器
│   ├── Resources/TestPage.html   # 内置测试页面
│   ├── MainForm.cs               # 带系统托盘的界面
│   └── appsettings.json          # 配置文件
├── packages/
│   └── scanwith-web-sdk/         # NPM 包 (TypeScript SDK)
│       ├── src/
│       │   ├── core/             # 核心客户端
│       │   ├── react/            # React hooks 和组件
│       │   ├── vue/              # Vue composables
│       │   └── types/            # TypeScript 类型定义
│       └── examples/             # 使用示例
├── ScanWithWeb_setup/            # 安装程序脚本 (Inno Setup)
├── assets/                       # 应用程序图标
└── .github/workflows/            # CI/CD 工作流
```

## 配置

编辑 `ScanWithWeb/appsettings.json`：

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

**注意：** SSL 证书会在首次运行时自动生成并安装到 Windows 受信任存储。

## V2 协议

### 请求
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

### 响应
```json
{
  "status": "success",
  "metadata": { "width": 2480, "height": 3508, "dpi": 300 },
  "data": "base64-image-data",
  "pageNumber": 1
}
```

### 可用操作

| 操作 | 描述 |
|------|------|
| `authenticate` | 获取会话令牌 |
| `list_scanners` | 获取可用扫描仪列表 |
| `select_scanner` | 选择扫描仪 |
| `get_capabilities` | 获取扫描仪能力 |
| `scan` | 开始扫描 |
| `stop_scan` | 停止当前扫描 |

### 扫描设置

| 设置 | 类型 | 默认值 | 描述 |
|------|------|--------|------|
| `dpi` | number | 200 | 分辨率 (DPI) |
| `pixelType` | string | "RGB" | 色彩模式 (RGB, Gray8, BlackWhite) |
| `paperSize` | string | "A4" | 纸张大小 (A4, Letter, Legal 等) |
| `duplex` | boolean | false | 双面扫描 |
| `useAdf` | boolean | true | 使用自动进纸器 |
| `maxPages` | number | -1 | 最大页数 (-1 = 无限制) |
| `showUI` | boolean | false | 显示扫描仪对话框 |

## Web SDK

TypeScript SDK 完全支持 React、Vue 和原生 JavaScript：

### 特性
- 完整的 TypeScript 支持和类型定义
- React hooks (`useScanClient`, `useScanners`, `useScan`)
- Vue 3 composables (`useFullScanner`, `useScanClient`)
- 事件驱动架构
- 自动重连支持
- 类型化的错误处理

### 安装

```bash
npm install scanwith-web-sdk
```

详细文档请参阅 [packages/scanwith-web-sdk/README.zh-CN.md](packages/scanwith-web-sdk/README.zh-CN.md)。

## 从源码构建

### Windows 服务

```bash
# 64位
dotnet publish ScanWithWeb/ScanWithWeb.csproj -c Release -r win-x64 -o dist/win-x64 --self-contained true

# 32位
dotnet publish ScanWithWeb/ScanWithWeb.csproj -c Release -r win-x86 -o dist/win-x86 --self-contained true
```

### 创建安装程序

参见 [ScanWithWeb_setup/README.md](ScanWithWeb_setup/README.md) 了解如何使用 Inno Setup 构建安装程序。

### Web SDK

```bash
cd packages/scanwith-web-sdk
npm install
npm run build
```

## CI/CD

本项目使用 GitHub Actions 进行自动构建：

- **构建测试**：在 push/PR 时验证构建是否成功
- **构建和发布**：推送版本标签时创建安装程序并发布

创建发布版本：
```bash
git tag v2.0.4
git push origin v2.0.4
```

## 系统要求

- Windows 10/11
- .NET 8.0 运行时（安装程序已包含）
- TWAIN 兼容扫描仪

## 浏览器支持

| 浏览器 | 版本 |
|--------|------|
| Chrome | 60+ |
| Firefox | 55+ |
| Safari | 11+ |
| Edge | 79+ |

## 许可证

MIT 许可证
