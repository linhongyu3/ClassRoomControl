@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

echo ========================================
echo 班级电脑控制助手 - 发布脚本
echo ========================================
echo.

set PROJECT_DIR=%~dp0
set PROJECT_FILE=%PROJECT_DIR%ClassroomControl.csproj
set OUTPUT_DIR=%PROJECT_DIR%publish
set APP_NAME=班级电脑控制助手
set VERSION=1.0.3

echo [1/6] 清理旧的发布文件...
if exist "%OUTPUT_DIR%" (
    rd /s /q "%OUTPUT_DIR%"
    echo 已删除旧的发布目录
)
echo.

echo [2/6] 创建发布目录...
mkdir "%OUTPUT_DIR%\%APP_NAME%"
echo 发布目录已创建
echo.

echo [3/6] 开始发布应用程序...
dotnet publish "%PROJECT_FILE%" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o "%OUTPUT_DIR%\%APP_NAME%"

if errorlevel 1 (
    echo 发布失败！
    pause
    exit /b 1
)
echo.

echo [4/6] 复制配置文件和图标...
copy "%PROJECT_DIR%logo.ico" "%OUTPUT_DIR%\%APP_NAME%\" >nul
echo 配置文件和图标已复制
echo.

echo [5/6] 创建安装说明文件...
(
echo %APP_NAME% v%VERSION%
echo.
echo 安装说明：
echo 1. 将整个文件夹复制到任意位置
echo 2. 双击 ClassroomControl.exe 运行程序
echo 3. 首次运行建议右键选择"以管理员身份运行"
echo.
echo 功能说明：
echo - 电源控制：关机、重启、睡眠、休眠
echo - 任务计划：设置每日定时任务和提醒
echo - 桌面整理：自动整理桌面文件和定时整理
echo - 前台优化：进程优先级管理和自动优化
echo - 应用管理：限制应用使用时间和密码保护
echo - 快速启动：自定义快捷启动应用
echo - 操作日志：记录所有操作历史
echo.
echo 注意事项：
echo - 程序需要 .NET 10.0 Desktop Runtime
echo - 某些功能需要管理员权限
echo - 开机自启功能可在设置中配置
echo.
echo 作者：Bilibili主页
echo https://space.bilibili.com/3546575265597760
) > "%OUTPUT_DIR%\%APP_NAME%\安装说明.txt"
echo 安装说明文件已创建
echo.

echo [6/6] 创建发布压缩包...
set ZIP_FILE=%PROJECT_DIR%%APP_NAME%_v%VERSION%_win-x64.zip
if exist "%ZIP_FILE%" del "%ZIP_FILE%"

powershell -Command "Compress-Archive -Path '%OUTPUT_DIR%\%APP_NAME%\*' -DestinationPath '%ZIP_FILE%' -Force"

if exist "%ZIP_FILE%" (
    echo 压缩包已创建：%ZIP_FILE%
) else (
    echo 警告：压缩包创建失败
)
echo.

echo ========================================
echo 发布完成！
echo ========================================
echo.
echo 发布位置：%OUTPUT_DIR%\%APP_NAME%\
echo 压缩包：%ZIP_FILE%
echo.
echo 按任意键退出...
pause >nul
```batch

@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

echo ========================================
echo 班级电脑控制助手 - 发布脚本
echo ========================================
echo.

set PROJECT_DIR=%~dp0
set PROJECT_FILE=%PROJECT_DIR%ClassroomControl.csproj
set OUTPUT_DIR=%PROJECT_DIR%publish
set APP_NAME=班级电脑控制助手
set VERSION=1.0.1

echo [1/6] 清理旧的发布文件...
if exist "%OUTPUT_DIR%" (
    rd /s /q "%OUTPUT_DIR%"
    echo 已删除旧的发布目录
)
echo.

echo [2/6] 创建发布目录...
mkdir "%OUTPUT_DIR%\%APP_NAME%"
echo 发布目录已创建
echo.

echo [3/6] 开始发布应用程序...
dotnet publish "%PROJECT_FILE%" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o "%OUTPUT_DIR%\%APP_NAME%"

if errorlevel 1 (
    echo 发布失败！
    pause
    exit /b 1
)
echo.

echo [4/6] 复制配置文件和图标...
copy "%PROJECT_DIR%logo.ico" "%OUTPUT_DIR%\%APP_NAME%\" >nul
echo 配置文件和图标已复制
echo.

echo [5/6] 创建安装说明文件...
(
echo %APP_NAME% v%VERSION%
echo.
echo 安装说明：
echo 1. 将整个文件夹复制到任意位置
echo 2. 双击 ClassroomControl.exe 运行程序
echo 3. 首次运行建议右键选择"以管理员身份运行"
echo.
echo 功能说明：
echo - 电源控制：关机、重启、睡眠、休眠
echo - 任务计划：设置每日定时任务和提醒
echo - 桌面整理：自动整理桌面文件和定时整理
echo - 前台优化：进程优先级管理和自动优化
echo - 应用管理：限制应用使用时间和密码保护
echo - 快速启动：自定义快捷启动应用
echo - 操作日志：记录所有操作历史
echo.
echo 注意事项：
echo - 程序需要 .NET 10.0 Desktop Runtime
echo - 某些功能需要管理员权限
echo - 开机自启功能可在设置中配置
echo.
echo 作者：Bilibili主页
echo https://space.bilibili.com/3546575265597760
) > "%OUTPUT_DIR%\%APP_NAME%\安装说明.txt"
echo 安装说明文件已创建
echo.

echo [6/6] 创建发布压缩包...
set ZIP_FILE=%PROJECT_DIR%%APP_NAME%_v%VERSION%_win-x64.zip
if exist "%ZIP_FILE%" del "%ZIP_FILE%"

powershell -Command "Compress-Archive -Path '%OUTPUT_DIR%\%APP_NAME%\*' -DestinationPath '%ZIP_FILE%' -Force"

if exist "%ZIP_FILE%" (
    echo 压缩包已创建：%ZIP_FILE%
) else (
    echo 警告：压缩包创建失败
)
echo.

echo ========================================
echo 发布完成！
echo ========================================
echo.
echo 发布位置：%OUTPUT_DIR%\%APP_NAME%\
echo 压缩包：%ZIP_FILE%
echo.
echo 按任意键退出...
pause >nul
```