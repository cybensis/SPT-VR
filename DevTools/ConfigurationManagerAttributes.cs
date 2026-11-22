namespace SptVrDevTools
{
    // Read by name by ConfigurationManager (and through it Integrated Configuration Manager): only the fields set are used
#pragma warning disable 0169, 0414, 0649
    internal sealed class ConfigurationManagerAttributes
    {
        public int? Order;          // higher shows first
        public bool? ReadOnly;
        public bool? IsAdvanced;
        public string DispName;
    }
#pragma warning restore 0169, 0414, 0649
}
