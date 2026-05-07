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
            text = Regex.Replace(text, @"[^A-Z0-9]", "");
            return text;
        }

        public string CorrectCharacters(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            char[] chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (_fixMap.TryGetValue(chars[i], out char corrected))
                {
                    chars[i] = corrected;
                }
            }
            return new string(chars);
        }

        public string ExtractTailCode(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            string normalized = Normalize(text);
            string corrected = CorrectCharacters(normalized);

            string tailCode = ExtractTailCodeFromRight(corrected);
            if (!string.IsNullOrEmpty(tailCode))
                return tailCode;

            tailCode = ExtractTailCodeByRegex(corrected);
            return tailCode;
        }

        private string ExtractTailCodeFromRight(string text)
        {
            if (text.Length < 4)
                return string.Empty;

            for (int i = text.Length - 4; i >= 0; i--)
            {
                string sub = text.Substring(i, 4);
                if (sub.All(char.IsDigit))
                    return sub;
            }

            return string.Empty;
        }

        private string ExtractTailCodeByRegex(string text)
        {
            Match m = Regex.Match(text, @"(\d{4})$");
            if (m.Success)
                return m.Groups[1].Value;

            m = Regex.Match(text, @"\d{4}");
            if (m.Success)
                return m.Groups[0].Value;

            return string.Empty;
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
