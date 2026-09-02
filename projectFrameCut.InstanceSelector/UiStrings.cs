using System.Globalization;
using Windows.ApplicationModel.Resources;

namespace projectFrameCut.InstanceSelector
{
    internal static class UiStrings
    {
        private static readonly ResourceLoader Loader = ResourceLoader.GetForViewIndependentUse();

        public static string Get(string key) => Loader.GetString(key);

        public static string Format(string key, params object[] args) =>
            string.Format(CultureInfo.CurrentCulture, Get(key), args);
    }
}
