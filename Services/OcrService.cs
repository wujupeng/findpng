using OpenCvSharp;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models.Local;
using Sdcb.PaddleInference;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ImageSearch.Services
{
    public class OcrService : IDisposable
    {
        private PaddleOcrAll? _ocr;
        private bool _isInitialized;
        private readonly object _lock = new object();

        public bool IsInitialized => _isInitialized;

        public void Initialize()
        {
            lock (_lock)
            {
                if (_isInitialized) return;

                // 优先从程序目录加载本地模型
                if (TryLoadLocalModel())
                {
                    _isInitialized = true;
                    return;
                }

                // 尝试自动下载模型到程序目录
                if (TryDownloadModel())
                {
                    if (TryLoadLocalModel())
                    {
                        _isInitialized = true;
                        return;
                    }
                }

                try
                {
                    // 使用默认的在线下载模型
                    _ocr = new PaddleOcrAll(LocalFullModels.ChineseV3);
                    _isInitialized = true;
                }
                catch (Exception)
                {
                    try
                    {
                        _ocr = new PaddleOcrAll(LocalFullModels.EnglishV3);
                        _isInitialized = true;
                    }
                    catch (Exception)
                    {
                        _isInitialized = false;
                    }
                }
            }
        }

        private bool TryLoadLocalModel()
        {
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string modelDir = Path.Combine(exeDir, "OcrModels");

                string detPath = Path.Combine(modelDir, "ch_PP-OCRv3_det_infer", "inference.pdmodel");
                string recPath = Path.Combine(modelDir, "ch_PP-OCRv3_rec_infer", "inference.pdmodel");
                string clsPath = Path.Combine(modelDir, "ch_ppocr_mobile_v2.0_cls_infer", "inference.pdmodel");

                if (File.Exists(detPath) && File.Exists(recPath) && File.Exists(clsPath))
                {
                    LogOcrAttempt($"Loading local model from: {modelDir}");
                    
                    _ocr = new PaddleOcrAll(LocalFullModels.ChineseV3, PaddleDevice.Mkldnn());
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogOcrAttempt($"Failed to load local model: {ex.Message}");
            }
            return false;
        }

        private bool TryDownloadModel()
        {
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string modelDir = Path.Combine(exeDir, "OcrModels");
                
                if (!Directory.Exists(modelDir))
                    Directory.CreateDirectory(modelDir);

                LogOcrAttempt("Downloading OCR models from Baidu Cloud...");

                DownloadAndExtract("https://paddleocr.bj.bcebos.com/PP-OCRv3/chinese/ch_PP-OCRv3_det_infer.tar", modelDir);
                DownloadAndExtract("https://paddleocr.bj.bcebos.com/PP-OCRv3/chinese/ch_PP-OCRv3_rec_infer.tar", modelDir);
                DownloadAndExtract("https://paddleocr.bj.bcebos.com/dygraph_v2.0/ch/ch_ppocr_mobile_v2.0_cls_infer.tar", modelDir);

                LogOcrAttempt("OCR models downloaded successfully");
                return true;
            }
            catch (Exception ex)
            {
                LogOcrAttempt($"Failed to download models: {ex.Message}");
                return false;
            }
        }

        private void DownloadAndExtract(string url, string targetDir)
        {
            var proxy = new WebProxy("127.0.0.1", 10808);
            var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true };

            using (var client = new HttpClient(handler))
            {
                string fileName = Path.GetFileName(url);
                string tempPath = Path.Combine(Path.GetTempPath(), fileName);

                LogOcrAttempt($"Downloading {fileName} via proxy 127.0.0.1:10808...");

                using (var stream = client.GetStreamAsync(url).Result)
                {
                    using (var fileStream = new FileStream(tempPath, FileMode.Create))
                    {
                        stream.CopyTo(fileStream);
                    }
                }

                LogOcrAttempt($"Extracting {fileName}...");

                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = $"-Command \"tar -xf '{tempPath}' -C '{targetDir}'\"";
                psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;

                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    process?.WaitForExit();
                }

                File.Delete(tempPath);
            }
        }

        public string RecognizeText(string imagePath)
        {
            if (!_isInitialized || _ocr == null)
            {
                Initialize();
            }

            if (_ocr == null)
                return string.Empty;

            using var originalMat = Cv2.ImRead(imagePath, ImreadModes.Color);
            
            if (originalMat.Empty())
                return string.Empty;

            LogOcrAttempt(imagePath);

            try
            {
                // 第一步：直接跑原图，获取置信度
                var result1 = _ocr.Run(originalMat);
                var text1 = CleanOcrResult(result1);
                var confidence1 = GetAverageConfidence(result1);
                
                // 如果置信度足够高，直接返回，不需要再跑增强处理
                if (confidence1 > 0.90f && !string.IsNullOrEmpty(text1))
                {
                    LogOcrAttempt($"  - High confidence ({confidence1:F2}), returning directly");
                    return text1;
                }

                // 置信度不足时，才做增强处理（仅灰度+CLAHE）
                using var gray = new Mat();
                Cv2.CvtColor(originalMat, gray, ColorConversionCodes.BGR2GRAY);
                using var clahe = Cv2.CreateCLAHE(2.0, new OpenCvSharp.Size(8, 8));
                using var enhanced = new Mat();
                clahe.Apply(gray, enhanced);
                
                var result2 = _ocr.Run(enhanced);
                var text2 = CleanOcrResult(result2);
                var confidence2 = GetAverageConfidence(result2);

                // 返回置信度更高的那个结果
                if (confidence2 >= confidence1 && !string.IsNullOrEmpty(text2))
                {
                    LogOcrAttempt($"  - Enhanced better ({confidence2:F2} vs {confidence1:F2})");
                    return text2;
                }
                
                LogOcrAttempt($"  - Original better ({confidence1:F2} vs {confidence2:F2})");
                return text1;
            }
            catch (Exception ex)
            {
                LogOcrAttempt($"  - OCR failed: {ex.Message}");
                return string.Empty;
            }
        }

        private float GetAverageConfidence(PaddleOcrResult result)
        {
            if (result?.Regions == null || result.Regions.Length == 0) 
                return 0f;
            return result.Regions.Average(r => r.Score);
        }

        private string CleanOcrResult(PaddleOcrResult result)
        {
            if (result == null) 
                return string.Empty;

            // 只过滤非法字符，按置信度排序拼接，不做任何字符替换
            var text = string.Join(" ", result.Regions
                .OrderByDescending(r => r.Score)
                .Select(r => r.Text));
            
            // 只保留字母、数字、空格和连字符
            return Regex.Replace(text.ToUpper(), @"[^A-Z0-9\s\-]", "");
        }

        private void LogOcrAttempt(string message)
        {
            try
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImageSearch", "Logs");
                if (!Directory.Exists(logDir))
                    Directory.CreateDirectory(logDir);

                var logPath = Path.Combine(logDir, "ocr_log.txt");
                using var writer = new StreamWriter(logPath, true);
                writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}");
            }
            catch { }
        }

        public void Dispose()
        {
            _ocr?.Dispose();
            _isInitialized = false;
        }
    }
}