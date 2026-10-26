using System;

namespace UI18n.Core.Config
{
    /// <summary>
    /// 按**原始文本**从 cfg 里取值，绕过 KSP 的 ConfigNode 预处理。
    /// <para>
    /// 为什么必须有它：KSP 的 <c>ConfigNode.PreFormatConfig</c> 在切行后**无条件**执行
    /// <c>if ((num2 = line.IndexOf("//")) != -1) line = line.Remove(num2);</c>
    /// （Assembly-CSharp.dll 反编译证据、行号级摘录见 docs/09 §6.5.1），
    /// 也就是任何含 <c>://</c> 的配置值在进入 ConfigNode 之前就被砍成 <c>https:</c>。
    /// **加引号也救不了**：截断发生在引号处理之前。
    /// 所以 <c>UpdateBaseUrl</c> 这类 URL 只能从文件原文再读一遍。
    /// </para>
    /// <para>
    /// 规则（刻意窄）：逐行扫第一个 <c>键 = 值</c>；整行 <c>//</c> 开头视为注释跳过；
    /// 值两侧的成对双引号剥掉；**不**对值里的 <c>//</c> 做任何处理（那正是要保留的东西）。
    /// 因此不要在同一行写 <c>//</c> 注释——它会被当成值的一部分。
    /// </para>
    /// </summary>
    public static class ConfigText
    {
        /// <summary>从文件读出 <paramref name="key"/> 的第一个值；文件不存在或没有该键返回 <c>null</c>。</summary>
        public static string ReadValue(string path, string key)
        {
            string text;
            try
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                {
                    return null;
                }

                text = System.IO.File.ReadAllText(path);
            }
            catch (Exception)
            {
                return null;
            }

            return ReadValueFromText(text, key);
        }

        /// <summary>同 <see cref="ReadValue"/>，但输入是文本（单测入口）。</summary>
        public static string ReadValueFromText(string text, string key)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key))
            {
                return null;
            }

            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r').Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.Length >= 2 && line[0] == '/' && line[1] == '/')
                {
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                string name = line.Substring(0, eq).Trim();
                if (!string.Equals(name, key, StringComparison.Ordinal))
                {
                    continue;
                }

                string value = line.Substring(eq + 1).Trim();
                if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                {
                    value = value.Substring(1, value.Length - 2).Trim();
                }

                return value.Length == 0 ? null : value;
            }

            return null;
        }
    }
}
