# 班级电脑控制助手 · ClassroomControl

面向教室场景的 Windows 桌面管控工具，帮助教师管理和控制班级电脑的使用。

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square)
![WPF](https://img.shields.io/badge/UI-WPF-0078D4?style=flat-square)
![Windows](https://img.shields.io/badge/Platform-Windows-00A4EF?style=flat-square)
![Version](https://img.shields.io/badge/version-2.0.0-66BB6A?style=flat-square)

---

## ✨ 项目功能

### 电源控制
- **关机 / 重启 / 休眠 / 注销** — 通过系统 API (`powrprof.dll` / `shutdown.exe`) 执行，自动获取 `SeShutdownPrivilege` 权限
- **定时关机** — 设定每日自动关机时刻，或通过倒计时窗口提醒后执行

### 应用使用限制
- **按时间段阻止** — 为指定程序（按进程名或路径匹配）设置可用时间段，非允许时段自动终止进程
- **临时放行** — 输入密码后临时启用被限制应用
- **多规则多时段** — 每个规则可配置多个时间段，支持跨午夜

### 桌面整理
- **一键整理** — 将桌面文件和文件夹移动到目标目录，同名文件自动追加时间戳
- **定时整理** — 设置多个自动执行时刻
- **灵活排除** — 可排除指定文件夹、扩展名和 `.lnk` 快捷方式

### 定时任务
- 4 种任务类型：**关机 / 重启 / 运行程序 / 执行命令**
- 每条任务可独立配置触发时间与提醒倒计时
- 每天仅触发一次，避免重复执行

### USB 设备管理
- 自动检测已插入的 USB 存储设备并显示型号与卷标
- 一键弹出，内置占用进程关闭机制（WMI `Dismount` + ShellExecute 双重方案）

### 系统信息与快捷操作
- 显示主机名、操作系统友好名（如 "Windows 11 Pro"）、电池/电量、运行时长
- 快捷按钮：Edge 浏览器、资源管理器、任务管理器、命令提示符、设置
- 自定义快速启动应用 — 支持从可执行文件提取图标或自定义图片

### 系统托盘
- 窗口关闭按钮默认最小化到托盘，避免误退出
- 托盘右键菜单：打开主界面、关机、重启、退出
- 自定义圆角样式与悬停高亮

### 安全与管控
- **日志密码保护** — 退出程序、清除日志、恢复默认设置、查看应用限制等操作需输入密码
- **万能密码支持** — 便于管理员绕过用户设置的密码
- **活动日志** — 自动记录关机、重启、添加应用、任务执行等操作（最多 200 条）
- **单实例** — 互斥体 (`Mutex`) 防止程序多开

### 更新与运维
- **自动更新检查** — 对接 GitHub Releases API，逐段版本号比较（支持 `v2.0.0` / `2.0.0`）
- **代理镜像下载** — 内置多个国内可用的 GitHub 代理镜像，提高下载成功率
- **开发者模式** — 可切换显示/隐藏调试信息面板

---

## 🛠 技术栈

| 类别 | 技术 |
|------|------|
| 框架 | .NET 10 / WPF |
| UI 样式 | ModernWpfUI 0.9.6 |
| 系统管理 | System.Management (WMI) |
| 托盘图标 | System.Windows.Forms.NotifyIcon |
| 序列化 | System.Text.Json |
| 平台 | Windows 7 SP1 及以上 |

---

## 🚀 快速开始

### 环境要求
- Windows 7 SP1 或更高版本（推荐 Windows 10 / 11）
- [.NET 10.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

### 构建
```bash
cd ClassroomControl
dotnet build -c Release
```

### 发布
```bash
dotnet publish -c Release -r win-x64 --self-contained false
```

---

## 📁 项目结构

```
ClassroomControl/
├── App.xaml(.cs)              # 入口、单实例检查、环境校验
├── AppConstants.cs            # 版本号、名称、GitHub 地址、代理镜像
├── MainWindow.xaml(.cs)       # 主窗口（多面板 Tab）
├── ShutdownReminderWindow     # 关机倒计时提醒（滑入动画）
├── SystemPowerService.cs      # 关机/重启/休眠/注销
├── TrayIconService.cs         # 系统托盘 & 自定义菜单渲染
├── Services/
│   ├── ActivityLogService     # 操作活动日志
│   ├── AppUsageService        # 应用使用限制监控
│   ├── DailyTaskService       # 定时任务调度
│   ├── DesktopOrganizerService # 桌面整理
│   ├── SchedulerService       # 简单定时关机
│   ├── UpdateService          # GitHub Release 自动更新
│   └── UsbDetectionService    # USB 设备检测与弹出
└── Converters/
    └── IndexToVisibilityConverter  # Tab 面板可见性切换
```

---

## 💾 数据存储位置

所有用户配置保存在 `%AppData%\ClassroomControl\`：

| 文件 | 用途 |
|------|------|
| `activity_log.json` | 操作活动日志 |
| `custom_apps.json` | 自定义快速启动应用 |
| `desktop_organizer_settings.json` | 桌面整理设置 |
| `scheduled_tasks.json` | 定时任务列表 |
| `app_usage_rules.json` | 应用限制规则 |
| `dev_mode.json` | 开发者模式状态 |
| `high_priority_processes.json` | 高优先级进程配置 |

> 密码文件使用加密存储，路径为 `%LocalAppData%\ClassroomControl\log_password.json`。

---

## 📝 发布说明

发布新版本时请注意：

1. 在 [`AppConstants.cs`](file:///d:/Product/ClassRoomControl/AppConstants.cs) 中更新 `AppVersion`（本项目版本号的唯一来源）
2. GitHub Release 的 tag 请使用 `vX.Y.Z` 或 `X.Y.Z` 格式，以便 `UpdateService` 正确识别

---

## 📄 许可证

本项目采用 **Apache License 2.0** 开源协议，完整条款见 [LICENSE](file:///d:/Product/ClassRoomControl/LICENSE) 文件。

```
Copyright 2026 linhongyu3

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
```
