# HTKIS图片内容检索系统

基于PaddleOCR的工业图片内容检索系统，支持从复杂工业编码中提取尾号并进行全文检索。

## 📦 功能特性

- **OCR文字识别**：基于PaddleOCR的高精度文字识别
- **工业编码后处理**：从复杂工业编码中智能提取尾号
- **全文检索**：支持SQLite FTS5全文搜索和模糊搜索
- **批量索引**：支持批量扫描和索引图片
- **内置模型**：集成完整的OCR模型，无需额外下载
- **窗口图标**：自定义程序图标

## 🚀 快速开始

### 环境要求

- Windows 10/11
- .NET 8.0 Runtime（自包含版本无需安装）

### 安装使用

1. 下载 `ImageSearch_v1.0.zip`
2. 解压到任意目录
3. 双击运行 `ImageSearch.exe`

### 使用步骤

1. **选择目录**：点击"选择目录"按钮，选择包含图片的文件夹
2. **建立索引**：点击"建立索引"按钮，程序会自动扫描并进行OCR识别
3. **搜索图片**：在搜索框输入关键词（如尾号"5565"），点击"搜索"按钮

## 🛠️ 开发环境

### 技术栈

- **框架**：WPF (.NET 8.0)
- **OCR引擎**：PaddleOCR (Sdcb.PaddleOCR)
- **数据库**：SQLite + FTS5
- **图像预处理**：OpenCvSharp4

### 项目结构

```
findpng/
├── Data/                    # 数据层
│   └── DatabaseService.cs   # 数据库服务
├── Services/                # 服务层
│   ├── OcrService.cs        # OCR识别服务
│   ├── OcrPostProcessor.cs  # OCR后处理服务
│   ├── ImageScannerService.cs # 图片扫描服务
│   └── SearchService.cs     # 搜索服务
├── MainWindow.xaml          # 主窗口界面
├── MainWindow.xaml.cs       # 主窗口逻辑
└── README.md                # 项目说明
```

## 📋 版本历史

### v1.0.0 (2026-05-08)

- ✅ 实现基础图片扫描和OCR识别功能
- ✅ 添加工业编码后处理算法
- ✅ 集成SQLite FTS5全文搜索
- ✅ 支持模糊搜索
- ✅ 添加批量索引功能
- ✅ 集成OCR模型到程序目录
- ✅ 添加代理支持
- ✅ 添加程序图标
- ✅ 代码优化和bug修复

## 🎯 核心功能说明

### OCR后处理

针对工业编码的特点，实现了以下后处理算法：

1. **文本清洗**：去除特殊字符和多余空格
2. **字符校正**：对OCR识别的字符进行校正
3. **尾号提取**：从复杂编码中提取尾部目标号码

### 搜索策略

系统采用双重搜索策略：

1. **模糊搜索**：优先使用LIKE模糊搜索，适合数字匹配
2. **全文搜索**：使用FTS5全文索引，支持中文、英文、数字混合搜索

## 📝 开发说明

### 构建项目

```bash
dotnet build -c Release
```

### 发布项目

```bash
dotnet publish -c Release -r win-x64 --self-contained true
```

### 打包发布

```bash
# 发布并打包
./Pack.bat
```

## 🤝 贡献指南

欢迎提交Issue和Pull Request！

## 📄 许可证

本项目仅供学习和研究使用。

## 🙏 致谢

- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) - 百度开源OCR引擎
- [Sdcb.PaddleOCR](https://github.com/sdcb/PaddleOCR) - PaddleOCR的.NET封装

---

**HTKIS图片内容检索系统** - 让工业图片检索更简单！
