using System;
using System.Collections.Generic;

namespace FastFind
{
    [Flags]
    internal enum FileCategory : byte
    {
        None = 0,
        Documents = 1,
        Images = 2,
        Videos = 4,
        Audio = 8,
        Archives = 16,
        Programs = 32,
        Code = 64,
        Other = 128,
        All = Documents | Images | Videos | Audio | Archives | Programs | Code | Other
    }

    /// <summary>Extension → broad file category, computed once at scan time and stored per node.</summary>
    internal static class FileTypes
    {
        private static readonly Dictionary<string, FileCategory> Map = BuildMap();

        public static readonly string[] Names = { "文档", "图片", "视频", "音频", "压缩包", "程序", "代码", "其他" };
        public static readonly FileCategory[] Values =
        {
            FileCategory.Documents, FileCategory.Images, FileCategory.Videos, FileCategory.Audio,
            FileCategory.Archives, FileCategory.Programs, FileCategory.Code, FileCategory.Other
        };

        public static FileCategory ClassifyName(string name)
        {
            int dot = name.LastIndexOf('.');
            if (dot <= 0 || dot >= name.Length - 1) return FileCategory.Other;
            string ext = name.Substring(dot + 1);
            FileCategory cat;
            if (Map.TryGetValue(ext, out cat)) return cat;
            return FileCategory.Other;
        }

        public static FileCategory ClassifyExtension(char[] pool, int off, int len)
        {
            if (len <= 0 || len > 16) return FileCategory.Other;
            var chars = new char[len];
            Array.Copy(pool, off, chars, 0, len);
            FileCategory cat;
            if (Map.TryGetValue(new string(chars), out cat)) return cat;
            return FileCategory.Other;
        }

        public static string Describe(int mask)
        {
            if (mask == 0) return "全部";
            var parts = new List<string>();
            for (int i = 0; i < Values.Length; i++)
                if ((mask & (int)Values[i]) != 0) parts.Add(Names[i]);
            if (parts.Count == 0) return "全部";
            if (parts.Count <= 2) return string.Join("+", parts.ToArray());
            return parts.Count + " 类";
        }

        private static Dictionary<string, FileCategory> BuildMap()
        {
            var d = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
            Add(d, FileCategory.Documents,
                "doc", "docx", "docm", "dot", "dotx", "pdf", "txt", "md", "markdown", "rtf", "odt", "ods", "odp",
                "xls", "xlsx", "xlsm", "xlsb", "csv", "tsv", "ppt", "pptx", "pptm", "pps", "ppsx", "wps", "wpt",
                "et", "dps", "pages", "numbers", "key", "epub", "mobi", "azw3", "chm", "tex");
            Add(d, FileCategory.Images,
                "jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "dib", "webp", "tif", "tiff", "svg", "ico",
                "heic", "heif", "raw", "cr2", "nef", "arw", "dng", "orf", "raf", "psd", "ai", "sketch", "xcf", "avif");
            Add(d, FileCategory.Videos,
                "mp4", "m4v", "mkv", "avi", "mov", "wmv", "flv", "webm", "mpg", "mpeg", "mpe", "ts", "m2ts", "3gp",
                "3g2", "rmvb", "rm", "vob", "ogv", "asf", "f4v", "mts");
            Add(d, FileCategory.Audio,
                "mp3", "wav", "flac", "aac", "ogg", "oga", "m4a", "wma", "ape", "opus", "mid", "midi", "amr", "aiff", "aif");
            Add(d, FileCategory.Archives,
                "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zst", "lz", "lzma", "cab", "iso", "img", "dmg",
                "apk", "jar", "war", "ear", "pak", "deb", "rpm", "wim", "vhd", "vhdx");
            Add(d, FileCategory.Programs,
                "exe", "msi", "msix", "appx", "bat", "cmd", "ps1", "psm1", "vbs", "vbe", "com", "scr", "dll", "sys",
                "ocx", "drv", "lnk", "url", "cpl", "msc", "reg", "inf");
            Add(d, FileCategory.Code,
                "cs", "csx", "js", "jsx", "mjs", "cjs", "ts", "tsx", "py", "pyw", "java", "kt", "kts", "c", "cc",
                "cpp", "cxx", "h", "hpp", "hh", "go", "rs", "php", "rb", "swift", "m", "mm", "scala", "lua", "pl",
                "r", "sh", "bash", "zsh", "fish", "html", "htm", "css", "scss", "sass", "less", "vue",
                "svelte", "sql", "json", "jsonc", "xml", "yml", "yaml", "toml", "ini", "cfg", "conf", "gradle",
                "cmake", "mk", "sol", "asm", "sln", "csproj", "vcxproj");
            return d;
        }

        private static void Add(Dictionary<string, FileCategory> map, FileCategory cat, params string[] extensions)
        {
            for (int i = 0; i < extensions.Length; i++)
            {
                if (extensions[i].IndexOf('?') >= 0) continue;
                map[extensions[i]] = cat;
            }
        }
    }
}

