// UI18n · 影子编译桩（uGUI / TMPro 侧）
// 只复刻我们打到的那两个文本出口：UnityEngine.UI.Text.set_text 与 TMPro.TMP_Text.set_text。
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace UnityEngine.UI
{
    public class Graphic : MonoBehaviour
    {
        public Color color;
        public bool raycastTarget;
    }

    public class MaskableGraphic : Graphic { }

    public class Text : MaskableGraphic
    {
        public string text;
        public Font font;
        public int fontSize;
        public FontStyle fontStyle;
        public TextAnchor alignment;
        public bool supportRichText;
        public float lineSpacing;
        public bool resizeTextForBestFit;
    }
}

namespace TMPro
{
    public enum FontStyles { Normal = 0, Bold = 1, Italic = 2, Underline = 4 }

    public enum TextAlignmentOptions
    {
        TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight
    }

    public class TMP_Asset : UnityEngine.Object { }

    public class TMP_FontAsset : TMP_Asset
    {
        public List<TMP_FontAsset> fallbackFontAssets;
        public List<TMP_FontAsset> fallbackFontAssetTable;
        public string faceInfo;
        public bool HasCharacter(char character) { return false; }
        public bool HasCharacter(char character, bool searchFallbacks) { return false; }
        public bool HasCharacter(char character, bool searchFallbacks, bool tryAddCharacter) { return false; }
        public bool HasCharacters(string text) { return false; }
    }

    public class TMP_Text : MaskableGraphic
    {
        public string text;
        public TMP_FontAsset font;
        public float fontSize;
        public FontStyles fontStyle;
        public TextAlignmentOptions alignment;
        public bool enableWordWrapping;
        public bool richText;
        public void ForceMeshUpdate() { }
        public void SetText(string text) { }
        public void SetText(string text, bool syncTextInputBox) { }
    }

    public class TextMeshProUGUI : TMP_Text { }

    public class TMP_Settings : UnityEngine.ScriptableObject
    {
        public static TMP_Settings instance;
        public TMP_FontAsset defaultFontAsset;
    }
}
