using System;

namespace SqlLense.Vsix
{
    /// <summary>Must match the symbols in SqlLensePackage.vsct.</summary>
    internal static class PackageIds
    {
        public const string PackageGuidString = "e27c024c-8b5a-45c5-8189-dc5dd0a3e8b2";
        public const string CommandSetGuidString = "b51c1e06-9893-4916-bfe0-d55730ffd58f";

        public static readonly Guid CommandSet = new Guid(CommandSetGuidString);

        public const int RefreshSchemaCommandId = 0x0100;
        public const int ConfigureCommandId = 0x0101;
    }
}
