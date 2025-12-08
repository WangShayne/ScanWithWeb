# ScanWithWeb 安装包制作指南

## GitHub Actions 自动构建（推荐）

代码推送到 GitHub 后，可以通过以下方式自动构建安装包：

### 方式一：创建 Tag 触发自动发布
```bash
git tag v2.0.5
git push origin v2.0.5
```
这将自动触发 GitHub Actions：
1. 编译 64 位和 32 位应用程序
2. 创建安装包
3. 创建 GitHub Release 并上传所有文件

### 方式二：手动触发
1. 进入 GitHub 仓库页面
2. 点击 **Actions** 标签
3. 选择 **Build and Release** 工作流
4. 点击 **Run workflow**
5. 输入版本号并运行

### Release 产物
| 文件 | 说明 |
|------|------|
| `ScanWithWeb_Setup_x64_v*.exe` | 64位安装包 |
| `ScanWithWeb_Setup_x86_v*.exe` | 32位安装包 |
| `ScanWithWeb_Portable_x64_v*.zip` | 64位便携版 |
| `ScanWithWeb_Portable_x86_v*.zip` | 32位便携版 |

---

## 本地构建方法

### 方法一：使用 Inno Setup

### 1. 安装 Inno Setup
下载地址：https://jrsoftware.org/isinfo.php

### 2. 一键构建
在 Windows 上运行：
```batch
cd ScanWithWeb_setup
build_installer.bat
```

这将自动：
- 编译 64 位和 32 位应用程序
- 生成两个安装包到 `dist/installer/` 目录

### 3. 手动构建
如果需要单独构建：

```batch
# 先编译应用
cd ScanWithWeb
dotnet publish -c Release -r win-x64 -o ..\dist\win-x64 --self-contained true
dotnet publish -c Release -r win-x86 -o ..\dist\win-x86 --self-contained true

# 然后用 Inno Setup 编译安装脚本
cd ..\ScanWithWeb_setup
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" ScanWithWeb_x64.iss
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" ScanWithWeb_x86.iss
```

## 输出文件

| 文件 | 说明 |
|------|------|
| `dist/installer/ScanWithWeb_Setup_x64_v2.0.5.exe` | 64位安装包 |
| `dist/installer/ScanWithWeb_Setup_x86_v2.0.5.exe` | 32位安装包 |

## 安装包功能

- 自动检测系统架构
- 支持中英文界面
- 可选创建桌面快捷方式
- 可选开机自启动
- 安装完成后自动启动服务
- 卸载时自动停止运行中的服务
- 管理员权限安装

## 方法二：使用 WiX Toolset

如果需要更专业的 MSI 安装包，可以使用 WiX：
https://wixtoolset.org/

## 方法三：使用 MSIX（Windows 10/11）

可以将应用打包为 MSIX 格式用于 Microsoft Store 或企业部署：
```bash
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true
# 然后使用 MSIX Packaging Tool 打包
```

## 数字签名（生产环境）

发布前建议对安装包进行数字签名：
```batch
signtool sign /f certificate.pfx /p password /t http://timestamp.digicert.com ScanWithWeb_Setup_x64_v2.0.5.exe
```
