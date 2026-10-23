UI18n / Fonts
==============

这个目录是 FontMode = Bundled 的落点：把准备好的 AssetBundle 放在这里，
然后把 GameData/UI18n/Config.cfg 里的 BundledFontPath 指向它（相对路径按
GameData/UI18n/ 解析，例如 Fonts/cjk.bundle）。

AssetBundle 里需要包含：
  - 一个 UnityEngine.Font（uGUI 用），或
  - 一个含 CJK 字形的 TMPro.TMP_FontAsset（TMP 用）

FontMode = OsFont（默认）时不读这里，走 Font.GetOSInstalledFontNames() 精确匹配系统字体；
FontMode = Off 时完全不注入字体（翻译照做，缺字由玩家自担）。
没有自定义字体需求时，这个目录保持只有本说明文件即可。
