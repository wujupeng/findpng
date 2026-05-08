using OpenCvSharp;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models.Local;
using Sdcb.PaddleInference;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
                    
                    // 使用LocalFullModels.ChineseV3，它会自动从OcrModels目录加载
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

                // 使用百度云国内镜像地址下载模型
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
            // 使用代理配置
            var proxy = new WebProxy("127.0.0.1", 10808);
            var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true };

            using (var client = new HttpClient(handler))
            {
                string fileName = Path.GetFileName(url);
                string tempPath = Path.Combine(Path.GetTempPath(), fileName);

                LogOcrAttempt($"Downloading {fileName} via proxy 127.0.0.1:10808...");

                // 下载文件
                using (var stream = client.GetStreamAsync(url).Result)
                {
                    using (var fileStream = new FileStream(tempPath, FileMode.Create))
                    {
                        stream.CopyTo(fileStream);
                    }
                }

                LogOcrAttempt($"Extracting {fileName}...");

                // 使用PowerShell解压tar文件
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

            // 记录日志
            LogOcrAttempt(imagePath);

            try
            {
                var results = new System.Collections.Generic.List<string>();
                
                // 方式1：原始图像直接识别
                var result1 = _ocr.Run(originalMat);
                results.Add(CleanOcrResult(result1));

                // 方式2：灰度处理
                using var grayMat = new Mat();
                Cv2.CvtColor(originalMat, grayMat, ColorConversionCodes.BGR2GRAY);
                
                // 方式2a：直接灰度识别
                var result2 = _ocr.Run(grayMat);
                results.Add(CleanOcrResult(result2));

                // 方式2b：对比度增强
                using var clahe = Cv2.CreateCLAHE(2.0, new OpenCvSharp.Size(8, 8));
                using var enhancedMat = new Mat();
                clahe.Apply(grayMat, enhancedMat);
                var result3 = _ocr.Run(enhancedMat);
                results.Add(CleanOcrResult(result3));

                // 方式3：自适应阈值二值化
                using var binaryMat = new Mat();
                Cv2.AdaptiveThreshold(
                    grayMat, 
                    binaryMat, 
                    255, 
                    AdaptiveThresholdTypes.GaussianC, 
                    ThresholdTypes.Binary, 
                    11, 
                    2
                );
                var result4 = _ocr.Run(binaryMat);
                results.Add(CleanOcrResult(result4));

                // 方式4：反色处理（针对深色背景浅色文字）
                using var invertedMat = new Mat();
                Cv2.BitwiseNot(grayMat, invertedMat);
                var result5 = _ocr.Run(invertedMat);
                results.Add(CleanOcrResult(result5));

                // 方式5：反色后的二值化
                using var invertedBinaryMat = new Mat();
                Cv2.AdaptiveThreshold(
                    invertedMat, 
                    invertedBinaryMat, 
                    255, 
                    AdaptiveThresholdTypes.GaussianC, 
                    ThresholdTypes.Binary, 
                    11, 
                    2
                );
                var result6 = _ocr.Run(invertedBinaryMat);
                results.Add(CleanOcrResult(result6));

                var combinedResult = string.Join(" ", results.Where(r => !string.IsNullOrEmpty(r)).Distinct());
                return combinedResult;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private string CleanOcrResult(PaddleOcrResult result)
        {
            if (result == null)
                return string.Empty;

            var allowedChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            
            var text = result.Text;
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var cleaned = new string(text.Where(c => allowedChars.Contains(c)).ToArray());
            
            // 字符纠正：处理OCR识别时常见的字符混淆
            cleaned = CorrectOcrCharacters(cleaned);
            
            return cleaned;
        }
        
        private string CorrectOcrCharacters(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            
            // OCR常见字符混淆纠正映射
            // 数字类混淆
            text = text.Replace('q', '9')    // q → 9
                       .Replace('Q', '9')    // Q → 9
                       .Replace('g', '9')    // g → 9
                       .Replace('G', '6')    // G → 6
                       .Replace('b', '6')    // b → 6
                       .Replace('B', '8')    // B → 8
                       .Replace('D', '0')    // D → 0
                       .Replace('O', '0')    // O → 0
                       .Replace('o', '0')    // o → 0
                       .Replace('I', '1')    // I → 1
                       .Replace('l', '1')    // l → 1
                       .Replace('|', '1')    // | → 1
                       .Replace('S', '5')    // S → 5
                       .Replace('s', '5')    // s → 5
                       .Replace('Z', '2')    // Z → 2
                       .Replace('z', '2');   // z → 2
            
            // 工业条码特殊纠正：尾部数字码常见错误
            // 9和6在条码中容易混淆，根据上下文进行智能纠正
            char[] chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                // 如果当前字符是9，检查前后是否有数字模式表明应该是6
                if (chars[i] == '9')
                {
                    // 检查是否在尾部数字码位置（通常是最后几位数字）
                    int distanceFromEnd = chars.Length - i;
                    if (distanceFromEnd <= 6 && distanceFromEnd >= 1)
                    {
                        // 检查前后是否有数字特征表明应该是6
                        bool shouldBeSix = false;
                        
                        // 检查前一个字符
                        if (i > 0 && char.IsDigit(chars[i - 1]))
                        {
                            int prevDigit = chars[i - 1] - '0';
                            // 如果前一个数字是5或6，当前更可能是6
                            if (prevDigit == 5 || prevDigit == 6)
                            {
                                shouldBeSix = true;
                            }
                        }
                        
                        // 检查后一个字符
                        if (!shouldBeSix && i < chars.Length - 1 && char.IsDigit(chars[i + 1]))
                        {
                            int nextDigit = chars[i + 1] - '0';
                            // 如果后一个数字是5或6，当前更可能是6
                            if (nextDigit == 5 || nextDigit == 6)
                            {
                                shouldBeSix = true;
                            }
                        }
                        
                        if (shouldBeSix)
                        {
                            chars[i] = '6';
                        }
                    }
                }
            }
            
            return new string(chars);
        }

        private void LogOcrAttempt(string imagePath)
        {
            try
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImageSearch", "Logs");
                if (!Directory.Exists(logDir))
                    Directory.CreateDirectory(logDir);

                var logPath = Path.Combine(logDir, "ocr_log.txt");
                using var writer = new StreamWriter(logPath, true);
                writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - OCR attempt: {Path.GetFileName(imagePath)}");
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
