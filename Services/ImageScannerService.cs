using ImageSearch.Data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ImageSearch.Services
{
    public class ImageScannerService
    {
        private readonly DatabaseService _dbService;
        private readonly OcrService _ocrService;
        private readonly string[] _supportedExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".tiff", ".tif" };
        private CancellationTokenSource? _cancellationTokenSource;
        private int _processedCount;
        private int _totalCount;

        public event EventHandler<ScanProgressEventArgs>? ProgressChanged;
        public event EventHandler? ScanCompleted;

        public ImageScannerService(DatabaseService dbService, OcrService ocrService)
        {
            _dbService = dbService;
            _ocrService = ocrService;
        }

        public async Task ScanAsync(string rootDirectory, bool skipExisting = true)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _processedCount = 0;

            try
            {
                var imageFiles = GetAllImageFiles(rootDirectory);
                _totalCount = imageFiles.Count;

                OnProgressChanged(0, "正在初始化OCR引擎...");
                _ocrService.Initialize();

                OnProgressChanged(0, "开始扫描图片...");

                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = _cancellationTokenSource.Token
                };

                var tasks = new ConcurrentQueue<Task>();

                Parallel.ForEach(imageFiles, parallelOptions, (filePath, state) =>
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        state.Break();
                        return;
                    }

                    ProcessImage(filePath, skipExisting);

                    var current = Interlocked.Increment(ref _processedCount);
                    var progress = (int)((current / (double)_totalCount) * 100);
                    OnProgressChanged(progress, $"已处理 {current}/{_totalCount}");
                });

                await Task.WhenAll(tasks);

                OnProgressChanged(100, "索引建立完成");
                ScanCompleted?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                OnProgressChanged(_processedCount, "扫描已取消");
            }
            catch (Exception ex)
            {
                OnProgressChanged(_processedCount, $"扫描出错: {ex.Message}");
            }
        }

        public void CancelScan()
        {
            _cancellationTokenSource?.Cancel();
        }

        private List<string> GetAllImageFiles(string rootDirectory)
        {
            var files = new List<string>();

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(rootDirectory))
                {
                    files.AddRange(GetAllImageFiles(directory));
                }

                files.AddRange(Directory.EnumerateFiles(rootDirectory)
                    .Where(file => _supportedExtensions.Contains(Path.GetExtension(file).ToLower())));
            }
            catch (UnauthorizedAccessException)
            {
            }

            return files;
        }

        private void ProcessImage(string filePath, bool skipExisting)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                var fileMd5 = CalculateMd5(filePath);

                // 检查是否已存在（通过MD5或路径）
                if (skipExisting)
                {
                    if (_dbService.IsMd5Exists(fileMd5))
                        return;

                    if (_dbService.IsFilePathExists(filePath))
                    {
                        // 检查文件是否被修改
                        var storedLastModified = _dbService.GetLastModified(filePath);
                        if (storedLastModified.HasValue && 
                            storedLastModified.Value >= fileInfo.LastWriteTime)
                        {
                            return;
                        }
                    }
                }

                // OCR识别
                var ocrText = _ocrService.RecognizeText(filePath);

                // 如果识别到文本，存入数据库
                if (!string.IsNullOrWhiteSpace(ocrText))
                {
                    _dbService.InsertImageIndex(filePath, ocrText, fileMd5, fileInfo.LastWriteTime);
                }
            }
            catch (Exception)
            {
            }
        }

        private string CalculateMd5(string filePath)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(filePath);
            var hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }

        private void OnProgressChanged(int progress, string message)
        {
            ProgressChanged?.Invoke(this, new ScanProgressEventArgs(progress, message));
        }
    }

    public class ScanProgressEventArgs : EventArgs
    {
        public int Progress { get; }
        public string Message { get; }

        public ScanProgressEventArgs(int progress, string message)
        {
            Progress = progress;
            Message = message;
        }
    }
}