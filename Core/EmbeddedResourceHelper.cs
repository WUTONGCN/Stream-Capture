using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace StreamCapture.Core
{
    /// <summary>
    /// 嵌入资源帮助类 - 从程序集中读取嵌入资源
    /// </summary>
    public static class EmbeddedResourceHelper
    {
        private static readonly Assembly _assembly = Assembly.GetExecutingAssembly();

        /// <summary>
        /// 读取嵌入资源为字符串
        /// </summary>
        public static string? ReadResourceAsString(string resourcePath)
        {
            try
            {
                // 资源名称格式: StreamCapture.platforms.json
                // 或 StreamCapture.image.douyin.png
                var fullResourceName = $"StreamCapture.{resourcePath.Replace("/", ".").Replace("\\", ".")}";

                using var stream = _assembly.GetManifestResourceStream(fullResourceName);
                if (stream == null)
                {
                    // 尝试查找资源
                    var availableResources = _assembly.GetManifestResourceNames();
                    foreach (var res in availableResources)
                    {
                        if (res.EndsWith(resourcePath.Replace("/", ".").Replace("\\", ".")))
                        {
                            using var foundStream = _assembly.GetManifestResourceStream(res);
                            if (foundStream != null)
                            {
                                using var sr = new StreamReader(foundStream);
                                return sr.ReadToEnd();
                            }
                        }
                    }
                    return null;
                }

                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"读取嵌入资源失败: {resourcePath}, 错误: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 读取嵌入资源为字节数组
        /// </summary>
        public static byte[]? ReadResourceAsBytes(string resourcePath)
        {
            try
            {
                var fullResourceName = $"StreamCapture.{resourcePath.Replace("/", ".").Replace("\\", ".")}";

                using var stream = _assembly.GetManifestResourceStream(fullResourceName);
                if (stream == null)
                {
                    // 尝试查找资源
                    var availableResources = _assembly.GetManifestResourceNames();
                    foreach (var res in availableResources)
                    {
                        if (res.EndsWith(resourcePath.Replace("/", ".").Replace("\\", ".")))
                        {
                            using var foundStream = _assembly.GetManifestResourceStream(res);
                            if (foundStream != null)
                            {
                                using var memoryStream = new MemoryStream();
                                foundStream.CopyTo(memoryStream);
                                return memoryStream.ToArray();
                            }
                        }
                    }
                    return null;
                }

                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"读取嵌入资源失败: {resourcePath}, 错误: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 从嵌入资源加载 BitmapImage
        /// </summary>
        public static BitmapImage? LoadImageFromResource(string resourcePath)
        {
            try
            {
                var imageBytes = ReadResourceAsBytes(resourcePath);
                if (imageBytes == null) return null;

                var bitmap = new BitmapImage();
                using (var ms = new MemoryStream(imageBytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();
                    bitmap.Freeze(); // 使其可在其他线程使用
                }
                return bitmap;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载图片资源失败: {resourcePath}, 错误: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 列出所有嵌入资源（调试用）
        /// </summary>
        public static string[] GetAllResourceNames()
        {
            return _assembly.GetManifestResourceNames();
        }
    }
}
