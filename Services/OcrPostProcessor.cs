using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ImageSearch.Services
{
    public class OcrPostProcessor
    {
        private readonly Dictionary<char, char> _fixMap = new()
        {
            {'O', '0'},
            {'Q', '0'},
            {'I', '1'},
            {'L', '1'},
            {'Z', '2'},
            {'S', '5'},
            {'B', '8'}
        };

        public string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            text = text.ToUpper();
            text = Regex.Replace(text, @"[^A-Z0-9\s\-]", "");
            return text;
        }

        public string CorrectCharacters(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            // 找到主要是数字的片段再纠错，而不是全局替换
            return Regex.Replace(text, @"[A-Z0-9]+", seg => CorrectSegment(seg.Value));
        }

        private string CorrectSegment(string seg)
        {
            // 如果这段超过80%是数字，则把混入的字母纠正为数字
            int digitCount = seg.Count(char.IsDigit);
            if (digitCount * 1.0 / seg.Length > 0.8)
            {
                return seg.Replace('O', '0').Replace('Q', '0')
                          .Replace('I', '1').Replace('L', '1')
                          .Replace('Z', '2')
                          .Replace('S', '5')
                          .Replace('B', '8');
            }
            // 否则保留字母原样（如序列号中的字母）
            return seg;
        }

        public string ExtractTailCode(string text, int minLen = 4, int maxLen = 8)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            string normalized = Normalize(text);
            string corrected = CorrectCharacters(normalized);

            // 找所有纯数字片段，取最长的末尾片段
            var matches = Regex.Matches(corrected, @"\d{" + minLen + @"," + maxLen + @"}");
            if (matches.Count == 0) 
                return string.Empty;
            
            // 优先返回最靠近末尾的数字串
            return matches[matches.Count - 1].Value;
        }

        public string ExtractBatchNo(string text, int length = 6)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            string normalized = Normalize(text);
            string corrected = CorrectCharacters(normalized);

            string pattern = $@"[A-Z0-9]{{{length}}}";
            MatchCollection matches = Regex.Matches(corrected, pattern);
            
            foreach (Match m in matches)
            {
                if (m.Value.Any(char.IsLetter) && m.Value.Any(char.IsDigit))
                    return m.Value;
            }

            return string.Empty;
        }

        public string ExtractSerialNo(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            string normalized = Normalize(text);
            string corrected = CorrectCharacters(normalized);

            Match m = Regex.Match(corrected, @"SN[\d]{4,8}");
            if (m.Success)
                return m.Value;

            m = Regex.Match(corrected, @"S/N[\d]{4,8}");
            if (m.Success)
                return m.Value.Replace("/", "");

            return string.Empty;
        }

        public ProcessingResult Process(string rawText)
        {
            string normalized = Normalize(rawText);
            string corrected = CorrectCharacters(normalized);
            
            return new ProcessingResult
            {
                RawText = rawText,
                NormalizedText = normalized,
                CorrectedText = corrected,
                TailCode = ExtractTailCode(corrected),
                BatchNo = ExtractBatchNo(corrected),
                SerialNo = ExtractSerialNo(corrected)
            };
        }
    }

    public class ProcessingResult
    {
        public string RawText { get; set; } = string.Empty;
        public string NormalizedText { get; set; } = string.Empty;
        public string CorrectedText { get; set; } = string.Empty;
        public string TailCode { get; set; } = string.Empty;
        public string BatchNo { get; set; } = string.Empty;
        public string SerialNo { get; set; } = string.Empty;
    }
}