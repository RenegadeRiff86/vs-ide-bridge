namespace VsIdeBridge.Services.Diagnostics;

internal static class BestPracticeRuleCatalog
{
    public static class BP1001
    {
        public const string Code = "BP1001";
        public const string HelpUri = ErrorListConstants.BP1001HelpUri;
        public static readonly int Threshold = ErrorListConstants.RepeatedStringThreshold;
    }

    public static class BP1002
    {
        public const string Code = "BP1002";
        public const string HelpUri = ErrorListConstants.BP1002HelpUri;
        public static readonly int Threshold = ErrorListConstants.RepeatedStringThreshold;
    }

    public static class BP1003
    {
        public const string Code = "BP1003";
        public const string HelpUri = ErrorListConstants.BP1003HelpUri;
        public static readonly int Threshold = ErrorListConstants.RepeatedNumberThreshold;
    }

    public static class BP1004
    {
        public const string Code = "BP1004";
        public const string HelpUri = ErrorListConstants.BP1004HelpUri;
        public static readonly int Threshold = ErrorListConstants.RepeatedNumberThreshold;
    }

    public static class BP1005
    {
        public const string Code = "BP1005";
        public const string HelpUri = ErrorListConstants.BP1005HelpUri;
    }

    public static class BP1006
    {
        public const string Code = "BP1006";
        public const string HelpUri = ErrorListConstants.BP1006HelpUri;
    }

    public static class BP1007
    {
        public const string Code = "BP1007";
        public const string HelpUri = ErrorListConstants.BP1007HelpUri;
    }

    public static class BP1008
    {
        public const string Code = "BP1008";
        public const string HelpUri = ErrorListConstants.BP1008HelpUri;
    }

    public static class BP1009
    {
        public const string Code = "BP1009";
        public const string HelpUri = ErrorListConstants.BP1009HelpUri;
    }

    public static class BP1010
    {
        public const string Code = "BP1010";
        public const string HelpUri = ErrorListConstants.BP1010HelpUri;
    }

    public static class BP1011
    {
        public const string Code = "BP1011";
        public const string HelpUri = ErrorListConstants.BP1011HelpUri;
    }

    public static class BP1012
    {
        public const string Code = "BP1012";
        public static readonly int ThresholdWarning = ErrorListConstants.FileTooLongWarningThreshold;
        public static readonly int ThresholdError = ErrorListConstants.FileTooLongErrorThreshold;
    }

    public static class BP1013
    {
        public const string Code = "BP1013";
        public static readonly int Threshold = ErrorListConstants.MethodTooLongThreshold;
    }

    public static class BP1014
    {
        public const string Code = "BP1014";
    }

    public static class BP1015
    {
        public const string Code = "BP1015";
        public static readonly int Threshold = ErrorListConstants.DeepNestingThreshold;
    }

    public static class BP1016
    {
        public const string Code = "BP1016";
        public static readonly int Threshold = ErrorListConstants.CommentedOutCodeThreshold;
    }

    public static class BP1017
    {
        public const string Code = "BP1017";
    }

    public static class BP1018
    {
        public const string Code = "BP1018";
        public static readonly int ThresholdMethods = ErrorListConstants.GodClassMethodThreshold;
        public static readonly int ThresholdFields = ErrorListConstants.GodClassFieldThreshold;
    }

    public static class BP1019
    {
        public const string Code = "BP1019";
        public const string HelpUri = ErrorListConstants.BP1019HelpUri;
    }

    public static class BP1020
    {
        public const string Code = "BP1020";
        public const string HelpUri = ErrorListConstants.BP1020HelpUri;
    }

    public static class BP1021
    {
        public const string Code = "BP1021";
        public const string HelpUri = ErrorListConstants.BP1021HelpUri;
    }

    public static class BP1022
    {
        public const string Code = "BP1022";
        public const string HelpUri = ErrorListConstants.BP1022HelpUri;
    }

    public static class BP1023
    {
        public const string Code = "BP1023";
        public const string HelpUri = ErrorListConstants.BP1023HelpUri;
        public static readonly int Threshold = ErrorListConstants.MacroOveruseThreshold;
    }

    public static class BP1024
    {
        public const string Code = "BP1024";
        public const string HelpUri = ErrorListConstants.BP1024HelpUri;
        public static readonly int Threshold = ErrorListConstants.DeepNestingThreshold;
    }

    public static class BP1025
    {
        public const string Code = "BP1025";
        public const string HelpUri = ErrorListConstants.BP1025HelpUri;
    }

    public static class BP1026
    {
        public const string Code = "BP1026";
        public const string HelpUri = ErrorListConstants.BP1026HelpUri;
    }

    public static class BP1027
    {
        public const string Code = "BP1027";
        public const string HelpUri = ErrorListConstants.BP1027HelpUri;
        public static readonly int PropertyThreshold = ErrorListConstants.PropertyBagPropertyThreshold;
        public static readonly int BehaviorThreshold = ErrorListConstants.PropertyBagBehaviorThreshold;
    }
}