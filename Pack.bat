@echo off
chcp 65001 >nul
echo ==============================================
echo          图像内容检索系统 - 打包脚本
echo ==============================================
echo.

set "PUBLISH_DIR=bin\Release\net8.0-windows\win-x64\publish"
set "ZIP_NAME=ImageSearch_Setup.zip"

echo 检查发布目录...
if not exist "%PUBLISH_DIR%" (
    echo 错误：发布目录不存在，请先运行发布命令
    echo 命令：dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
    pause
    exit /b 1
)

echo.
echo 正在创建安装包...

powershell -Command "Compress-Archive -Path '%PUBLISH_DIR%\*' -DestinationPath '%ZIP_NAME%' -Force"

if exist "%ZIP_NAME%" (
    echo.
    echo 打包成功！
    echo 安装包位置: %cd%\%ZIP_NAME%
) else (
    echo.
    echo 错误：打包失败
    pause
    exit /b 1
)

echo.
echo ==============================================
echo              打包完成！
echo ==============================================
echo.
echo 安装包已创建: %ZIP_NAME%
echo 解压后运行 Install.bat 进行安装
echo.
pause
