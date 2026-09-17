using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using StreamCapture.Models;

namespace StreamCapture.Core
{
    /// <summary>
    /// 结果导出器
    /// </summary>
    public class ResultExporter
    {
        /// <summary>
        /// 导出为JSON
        /// </summary>
        public static void ExportToJson(List<CaptureResult> results, string filePath)
        {
            try
            {
                var json = JsonConvert.SerializeObject(results, Formatting.Indented);
                File.WriteAllText(filePath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                throw new Exception($"导出JSON失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 导出为文本
        /// </summary>
        public static void ExportToText(List<CaptureResult> results, string filePath)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("========================================");
                sb.AppendLine("         推流地址捕获结果");
                sb.AppendLine($"         生成时间: {DateTime.Now}");
                sb.AppendLine("========================================");
                sb.AppendLine();

                var grouped = results.GroupBy(r => r.Platform);

                foreach (var group in grouped)
                {
                    sb.AppendLine($"【{group.Key}】");
                    sb.AppendLine(new string('-', 50));

                    foreach (var result in group)
                    {
                        sb.AppendLine($"时间: {result.Timestamp:yyyy-MM-dd HH:mm:ss}");
                        sb.AppendLine($"协议: {result.Protocol}");
                        sb.AppendLine($"推流地址: {result.Url}");

                        if (!string.IsNullOrEmpty(result.StreamKey))
                        {
                            sb.AppendLine($"流密钥: {result.StreamKey}");
                        }

                        if (result.Parameters.Any())
                        {
                            sb.AppendLine("参数:");
                            foreach (var param in result.Parameters)
                            {
                                sb.AppendLine($"  {param.Key} = {param.Value}");
                            }
                        }

                        sb.AppendLine();
                    }

                    sb.AppendLine();
                }

                File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                throw new Exception($"导出文本失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 导出为CSV
        /// </summary>
        public static void ExportToCsv(List<CaptureResult> results, string filePath)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("时间,平台,协议,推流地址,流密钥");

                foreach (var result in results)
                {
                    var streamKey = result.StreamKey.Replace(",", ";");
                    var url = result.Url.Replace(",", ";");
                    sb.AppendLine($"{result.Timestamp:yyyy-MM-dd HH:mm:ss},{result.Platform},{result.Protocol},{url},{streamKey}");
                }

                File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                throw new Exception($"导出CSV失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 导出为Markdown
        /// </summary>
        public static void ExportToMarkdown(List<CaptureResult> results, string filePath)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# 推流地址捕获结果");
                sb.AppendLine();
                sb.AppendLine($"**生成时间**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();

                var grouped = results.GroupBy(r => r.Platform);

                foreach (var group in grouped)
                {
                    sb.AppendLine($"## {group.Key}");
                    sb.AppendLine();
                    sb.AppendLine("| 时间 | 协议 | 推流地址 | 流密钥 |");
                    sb.AppendLine("|------|------|----------|--------|");

                    foreach (var result in group)
                    {
                        var url = result.Url.Replace("|", "\\|");
                        var streamKey = result.StreamKey.Replace("|", "\\|");
                        sb.AppendLine($"| {result.Timestamp:HH:mm:ss} | {result.Protocol} | {url} | {streamKey} |");
                    }

                    sb.AppendLine();
                }

                File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                throw new Exception($"导出Markdown失败: {ex.Message}");
            }
        }
    }
}
