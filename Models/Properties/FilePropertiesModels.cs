using System;

namespace FastExplorer
{
    public enum PropertyTargetType
    {
        SingleFile,
        SingleFolder,
        MultipleItems,
        Drive
    }

    public class SecurityPrincipalPermission
    {
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Glyph { get; set; } = "\uE77B"; // ユーザーまたはグループアイコン
        public bool IsGroup { get; set; }

        public bool FullControl { get; set; }
        public bool Modify { get; set; }
        public bool ReadAndExecute { get; set; }
        public bool Read { get; set; }
        public bool Write { get; set; }
        public bool Special { get; set; }
    }

    public class DigitalSignatureItem
    {
        public string SignerName { get; set; } = string.Empty;
        public string DigestAlgorithm { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
    }

    public class FileDetailProperty
    {
        public string Category { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public bool IsHeader => string.IsNullOrEmpty(Value);
    }
}
